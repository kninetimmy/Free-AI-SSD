#!/bin/bash
#
# mac-unblock.command
#
# Strips the com.apple.quarantine xattr from the FreeAiSsd macOS apps so the
# unsigned beta launches without the bogus "FreeAiSsd is damaged and can't be
# opened" Gatekeeper dialog that Safari triggers on the downloaded ZIP.
#
# This is a temporary convenience until the signed/notarized release ships —
# it does exactly what the two manual `xattr -dr com.apple.quarantine ...`
# commands in the README do, but finds the apps for you.
#
# Usage:
#   - Double-click in Finder, or
#   - Run in Terminal:  bash mac-unblock.command  [optional folder ...]
#
# With no arguments it searches your script folder, ~/Downloads, ~/Desktop,
# the current directory, and every mounted /Volumes/* drive. Pass one or more
# folders to search only those instead.
#
set -u

app_names=("PrepApp.app" "Runner.app")
search_depth=5

printf 'FreeAiSsd unblock — stripping com.apple.quarantine\n'

# --- Resolve search roots ---------------------------------------------------
if [ "$#" -gt 0 ]; then
  roots=("$@")
else
  script_dir="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
  roots=("$script_dir" "$HOME/Downloads" "$HOME/Desktop" "$PWD")
  for vol in /Volumes/*; do
    [ -d "$vol" ] && roots+=("$vol")
  done
fi

# De-dupe roots (Downloads / PWD / script dir often overlap).
unique_roots=()
for r in "${roots[@]}"; do
  [ -d "$r" ] || continue
  skip=0
  for u in "${unique_roots[@]:-}"; do
    [ "$r" = "$u" ] && { skip=1; break; }
  done
  [ "$skip" -eq 0 ] && unique_roots+=("$r")
done

if [ "${#unique_roots[@]}" -eq 0 ]; then
  printf '  ✗ none of the search folders exist. Pass a path, e.g.:\n'
  printf '      bash "%s" /path/to/extracted/folder\n' "${BASH_SOURCE[0]:-$0}"
  exit 1
fi

# --- Find and unblock the apps ----------------------------------------------
stripped=()
found_any=0
for name in "${app_names[@]}"; do
  while IFS= read -r app; do
    [ -z "$app" ] && continue
    # Skip if we already handled this exact bundle.
    already=0
    for s in "${stripped[@]:-}"; do
      [ "$s" = "$app" ] && { already=1; break; }
    done
    [ "$already" -eq 1 ] && continue

    if xattr -dr com.apple.quarantine "$app" 2>/dev/null; then
      printf '  ✓ %s\n' "$app"
      stripped+=("$app")
      found_any=1
    else
      printf '  ✗ failed (permission?): %s\n' "$app"
    fi
  done < <(find "${unique_roots[@]}" -maxdepth "$search_depth" -name "$name" -type d 2>/dev/null)
done

# --- Report -----------------------------------------------------------------
if [ "$found_any" -eq 0 ]; then
  printf '\nNo PrepApp.app or Runner.app found in:\n'
  for r in "${unique_roots[@]}"; do printf '  %s\n' "$r"; done
  printf '\nExtract the download (or plug in the SSD), then run this again —\n'
  printf 'or point it straight at the folder:\n'
  printf '    bash "%s" /path/to/extracted/folder\n' "${BASH_SOURCE[0]:-$0}"
  exit 1
fi

printf '\nDone. The app(s) above now launch normally on double-click.\n'
exit 0
