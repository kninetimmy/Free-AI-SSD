#!/usr/bin/env bash
#
# build-mac-tesseract-bundle.sh — assemble a relocatable, offline macOS Tesseract
# bundle for Free-AI-SSD's opt-in PDF-image OCR (Phase 1 mac parity, task #91).
#
# WHY THIS EXISTS
# ---------------
# There is no drop-in portable arm64 Tesseract: Homebrew/MacPorts binaries hardcode
# Cellar / /opt paths and are not relocatable. This script copies the `tesseract`
# binary plus its full transitive dylib closure out of the Homebrew Cellar, rewrites
# every Mach-O load command to be `@executable_path`-relative (via install_name_tool),
# lays in the `tessdata/` language data + the `tsv` config so the layout mirrors the
# Windows bundle, and produces a `.tar.gz` (the mac archive convention, matching Piper).
#
# The resulting archive is uploaded ONCE to the frozen `ocr-tools-tesseract-5.4.0`
# release tag; its real URL + SHA-256 + byte size are then pinned in TesseractCatalog.
# Nothing here invents a hash — the script prints the real SHA-256 + size at the end.
#
# USAGE
#   brew install tesseract           # provides tesseract + leptonica + image dylibs
#   tools/mac-ocr-bundle/build-mac-tesseract-bundle.sh [OUTPUT_DIR]
#
# OUTPUT
#   <OUTPUT_DIR>/tesseract-<version>-osx-<arch>.tar.gz   (default OUTPUT_DIR: ./dist)
#   plus the SHA-256 and byte size printed for pasting into TesseractCatalog.
#
# REQUIREMENTS: a Mac with Homebrew, `tesseract` installed, and the Xcode command
# line tools (otool / install_name_tool / codesign).

set -euo pipefail

# ----------------------------------------------------------------------------
# 0. Preconditions
# ----------------------------------------------------------------------------
die() { echo "ERROR: $*" >&2; exit 1; }
note() { echo ">> $*"; }

command -v brew >/dev/null 2>&1 || die "Homebrew not found on PATH."
command -v otool >/dev/null 2>&1 || die "otool not found (install Xcode command line tools)."
command -v install_name_tool >/dev/null 2>&1 || die "install_name_tool not found (install Xcode command line tools)."

TESS_BIN="$(command -v tesseract || true)"
[ -n "$TESS_BIN" ] || die "tesseract not on PATH — run 'brew install tesseract' first."
# Resolve through any wrapper symlink to the real Cellar binary.
TESS_BIN="$(readlink -f "$TESS_BIN" 2>/dev/null || python3 -c 'import os,sys;print(os.path.realpath(sys.argv[1]))' "$TESS_BIN")"
[ -x "$TESS_BIN" ] || die "Resolved tesseract path is not executable: $TESS_BIN"

ARCH="$(uname -m)"   # arm64 | x86_64
case "$ARCH" in
  arm64)  ARCH_TAG="arm64" ;;
  x86_64) ARCH_TAG="x64"   ;;
  *) die "Unsupported architecture: $ARCH" ;;
esac

TESS_VERSION="$(tesseract --version 2>&1 | head -1 | awk '{print $2}')"
[ -n "$TESS_VERSION" ] || die "Could not determine tesseract version."

BREW_PREFIX="$(brew --prefix)"
TESSDATA_SRC="$(brew --prefix tesseract)/share/tessdata"
[ -d "$TESSDATA_SRC" ] || TESSDATA_SRC="$BREW_PREFIX/share/tessdata"
[ -d "$TESSDATA_SRC" ] || die "tessdata dir not found under $BREW_PREFIX/share."

OUTPUT_DIR="${1:-$(pwd)/dist}"
mkdir -p "$OUTPUT_DIR"

STAGE="$(mktemp -d)/tesseract"
mkdir -p "$STAGE"
trap 'rm -rf "$(dirname "$STAGE")"' EXIT

note "tesseract binary : $TESS_BIN"
note "version          : $TESS_VERSION ($ARCH_TAG)"
note "tessdata source  : $TESSDATA_SRC"
note "brew prefix      : $BREW_PREFIX"
note "staging into     : $STAGE"

# ----------------------------------------------------------------------------
# 1. Copy the tesseract binary
# ----------------------------------------------------------------------------
cp "$TESS_BIN" "$STAGE/tesseract"
chmod u+w "$STAGE/tesseract"

# ----------------------------------------------------------------------------
# 2. Recursively collect the dylib closure rooted at the binary.
#    We only relocate libraries that live under the Homebrew prefix (or any
#    /opt|/usr/local Cellar path) — system libs under /usr/lib and
#    /System/Library are guaranteed present on every Mac and must stay absolute.
# ----------------------------------------------------------------------------
# Resolve an otool -L dependency token to the real on-disk source file that we
# must bundle, or return 1 if it is an OS-provided library we should leave alone.
# Handles both absolute Homebrew paths AND @rpath/@loader_path/@executable_path
# references (e.g. webp's libsharpyuv) by basename-locating them under the brew
# prefix — everything we ship is Homebrew, so a basename search is unambiguous.
resolve_source() {
  local dep="$1" base cand
  case "$dep" in
    /usr/lib/*|/System/*) return 1 ;;                       # OS-provided — leave absolute
    "$BREW_PREFIX"/*|/opt/homebrew/*|/usr/local/Cellar/*|/opt/local/*)
      [ -f "$dep" ] && { echo "$dep"; return 0; }
      return 1 ;;
    @rpath/*|@loader_path/*|@executable_path/*)
      base="$(basename "$dep")"
      for cand in "$BREW_PREFIX/lib/$base" $BREW_PREFIX/opt/*/lib/"$base"; do
        [ -f "$cand" ] && { echo "$cand"; return 0; }
      done
      return 1 ;;                                           # unresolved @ ref → assume system
    *) return 1 ;;
  esac
}

# macOS ships bash 3.2 (no associative arrays), so the "already collected" set is
# a newline-delimited file of basenames. Each collected dylib is copied next to the
# binary as it is discovered; COLLECTED_FILE lets rewrite() test membership later.
COLLECTED_FILE="$(mktemp)"
trap 'rm -rf "$(dirname "$STAGE")" "$COLLECTED_FILE"' EXIT

is_collected() { grep -qxF "$1" "$COLLECTED_FILE" 2>/dev/null; }

deps_of() {
  # Print the absolute dependency paths of a Mach-O file (skip the file's own id line).
  otool -L "$1" | tail -n +2 | awk '{print $1}'
}

collect() {
  local file="$1"
  local dep base src
  while IFS= read -r dep; do
    [ -n "$dep" ] || continue
    if src="$(resolve_source "$dep")"; then
      base="$(basename "$dep")"
      if ! is_collected "$base"; then
        echo "$base" >> "$COLLECTED_FILE"
        cp "$src" "$STAGE/$base"
        chmod u+w "$STAGE/$base"
        note "  + $base   ($src)"
        collect "$src"   # chase transitive deps from the real file
      fi
    fi
  done < <(deps_of "$file")
}

note "Resolving dylib closure..."
collect "$TESS_BIN"
note "Bundled $(wc -l < "$COLLECTED_FILE" | tr -d ' ') dylibs."

# ----------------------------------------------------------------------------
# 3. Rewrite load commands to @executable_path-relative.
#    For the binary AND every bundled dylib:
#      - set each dylib's own id to @executable_path/<name>
#      - rewrite each reference to a bundled dep to @executable_path/<name>
#    @executable_path resolves to the dir containing `tesseract`, and since the
#    dylibs sit alongside it, every load resolves offline with no rpath search.
# ----------------------------------------------------------------------------
rewrite() {
  local file="$1"
  local fname; fname="$(basename "$file")"
  # If this is a dylib, fix its install id. (install_name_tool warns that it
  # invalidates the ad-hoc signature; that's expected — we re-sign in step 3b.)
  if [[ "$fname" == *.dylib ]]; then
    install_name_tool -id "@executable_path/$fname" "$file" 2>/dev/null
  fi
  # Rewrite every reference that points at a bundled dylib.
  local dep base
  while IFS= read -r dep; do
    [ -n "$dep" ] || continue
    base="$(basename "$dep")"
    if is_collected "$base"; then
      install_name_tool -change "$dep" "@executable_path/$base" "$file" 2>/dev/null
    fi
  done < <(deps_of "$file")
}

note "Rewriting load commands to @executable_path..."
rewrite "$STAGE/tesseract"
while IFS= read -r base; do
  [ -n "$base" ] || continue
  rewrite "$STAGE/$base"
done < "$COLLECTED_FILE"

# Re-sign ad-hoc: editing load commands invalidates the existing (ad-hoc) code
# signature on Apple Silicon, which makes dyld refuse to load the binary. An
# ad-hoc re-sign restores loadability without requiring a Developer ID.
if command -v codesign >/dev/null 2>&1; then
  note "Ad-hoc re-signing binary + dylibs..."
  while IFS= read -r base; do
    [ -n "$base" ] || continue
    codesign --remove-signature "$STAGE/$base" 2>/dev/null || true
    codesign -s - -f "$STAGE/$base" >/dev/null 2>&1 || die "codesign failed for $base"
  done < "$COLLECTED_FILE"
  codesign --remove-signature "$STAGE/tesseract" 2>/dev/null || true
  codesign -s - -f "$STAGE/tesseract" >/dev/null 2>&1 || die "codesign failed for tesseract"
fi

# ----------------------------------------------------------------------------
# 4. Lay in tessdata: eng + osd traineddata + the tsv config.
#    Mirrors the Windows bundle so TesseractOcrService finds tessdata/configs/tsv.
# ----------------------------------------------------------------------------
mkdir -p "$STAGE/tessdata/configs"
for lang in eng osd; do
  src="$TESSDATA_SRC/$lang.traineddata"
  [ -f "$src" ] || die "Missing $lang.traineddata in $TESSDATA_SRC (brew tesseract should ship eng+osd)."
  cp "$src" "$STAGE/tessdata/$lang.traineddata"
done
# The `tsv` config tells Tesseract to emit per-word TSV (what TesseractOcrService parses).
if [ -f "$TESSDATA_SRC/configs/tsv" ]; then
  cp "$TESSDATA_SRC/configs/tsv" "$STAGE/tessdata/configs/tsv"
else
  # Homebrew sometimes omits the stock configs; write the canonical one-liner.
  printf 'tessedit_create_tsv 1\n' > "$STAGE/tessdata/configs/tsv"
  note "tsv config not found upstream — wrote canonical 'tessedit_create_tsv 1'."
fi

# ----------------------------------------------------------------------------
# 5. Strip any quarantine xattr that may have ridden along, then smoke-test the
#    relocated binary IN PLACE (TESSDATA_PREFIX pointed at the staged tessdata).
# ----------------------------------------------------------------------------
xattr -dr com.apple.quarantine "$STAGE" 2>/dev/null || true

note "Verifying relocation (no Homebrew paths and no leftover @rpath in any bundled Mach-O)..."
for f in "$STAGE/tesseract" "$STAGE"/*.dylib; do
  if otool -L "$f" | tail -n +2 | awk '{print $1}' \
       | grep -Eq "$BREW_PREFIX|/opt/homebrew|Cellar|@rpath|@loader_path"; then
    echo "Offending file: $f"
    otool -L "$f"
    die "$(basename "$f") still has non-relocatable load commands — bundle is NOT portable."
  fi
done

note "Smoke-testing OCR on a generated test image..."
SMOKE_DIR="$(mktemp -d)"
if command -v python3 >/dev/null 2>&1; then
  # No external image libs guaranteed; --version + list-langs is the offline-safe check.
  TESSDATA_PREFIX="$STAGE/tessdata" "$STAGE/tesseract" --list-langs 2>&1 | sed 's/^/   /' || \
    die "Relocated tesseract failed to run --list-langs."
fi
rm -rf "$SMOKE_DIR"

# ----------------------------------------------------------------------------
# 6. Pack the tar.gz, structure-preserving (no top-level wrapper dir).
#    Entries: ./tesseract, ./*.dylib, ./tessdata/... — matches the Windows zip
#    layout so TesseractStagingService's structure-preserving extract lands it
#    correctly.
# ----------------------------------------------------------------------------
ARCHIVE_NAME="tesseract-${TESS_VERSION}-osx-${ARCH_TAG}.tar.gz"
ARCHIVE_PATH="$OUTPUT_DIR/$ARCHIVE_NAME"
note "Packing $ARCHIVE_PATH ..."
# -C into the stage so paths are relative (no leading stage dir). Deterministic-ish.
tar -czf "$ARCHIVE_PATH" -C "$STAGE" .

# ----------------------------------------------------------------------------
# 7. Report the real catalog values.
# ----------------------------------------------------------------------------
SHA256="$(shasum -a 256 "$ARCHIVE_PATH" | awk '{print $1}')"
SIZE="$(stat -f%z "$ARCHIVE_PATH")"

cat <<EOF

============================================================
  macOS Tesseract bundle assembled
============================================================
  archive   : $ARCHIVE_PATH
  filename  : $ARCHIVE_NAME
  tesseract : $TESS_VERSION ($ARCH_TAG)
  SHA-256   : $SHA256
  size      : $SIZE bytes

  Next:
   1. Upload '$ARCHIVE_NAME' to the frozen release tag
      'ocr-tools-tesseract-5.4.0' (DO NOT re-publish an existing asset).
   2. Pin these exact values into shared/Prereqs/TesseractCatalog.cs:
        ArchiveFileName = "$ARCHIVE_NAME"
        Sha256          = "$SHA256"
        SizeBytes       = ${SIZE}L
        (Url = https://github.com/kninetimmy/Free-AI-SSD/releases/download/ocr-tools-tesseract-5.4.0/$ARCHIVE_NAME)
============================================================
EOF
