#!/usr/bin/env bash
# Reproducible icon build: renders all PNG sizes via IconRenderer.swift, then
# assembles AppIcon.icns (macOS) and AppIcon.ico (Windows).
#
# Run from repo root or this directory. Output lands in assets/icon/.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
cd "$here"

work="$(mktemp -d -t freeai-icon-build)"
trap 'rm -rf "$work"' EXIT

iconset="$work/AppIcon.iconset"
mkdir -p "$iconset"

render() {
    swift IconRenderer.swift "$1" "$2"
}

# macOS iconset: required {1x,2x} pairs at 16/32/128/256/512.
render 16   "$iconset/icon_16x16.png"
render 32   "$iconset/icon_16x16@2x.png"
render 32   "$iconset/icon_32x32.png"
render 64   "$iconset/icon_32x32@2x.png"
render 128  "$iconset/icon_128x128.png"
render 256  "$iconset/icon_128x128@2x.png"
render 256  "$iconset/icon_256x256.png"
render 512  "$iconset/icon_256x256@2x.png"
render 512  "$iconset/icon_512x512.png"
render 1024 "$iconset/icon_512x512@2x.png"

iconutil --convert icns --output AppIcon.icns "$iconset"

# Windows .ico: standard sizes; PNG-embedded for 256.
icoset="$work/icoset"
mkdir -p "$icoset"
render 16  "$icoset/16.png"
render 32  "$icoset/32.png"
render 48  "$icoset/48.png"
render 64  "$icoset/64.png"
render 128 "$icoset/128.png"
render 256 "$icoset/256.png"
python3 ico-builder.py AppIcon.ico \
    "$icoset/16.png" "$icoset/32.png" "$icoset/48.png" \
    "$icoset/64.png" "$icoset/128.png" "$icoset/256.png"

# Master 1024 PNG -- handy for README, GitHub release art, etc.
render 1024 AppIcon.png

echo
echo "built:"
ls -la AppIcon.icns AppIcon.ico AppIcon.png
