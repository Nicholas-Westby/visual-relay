#!/usr/bin/env bash
# Regenerate the full macOS .iconset from the committed 1024x1024 master.
#
# The iconset is the conventional input to `iconutil -c icns`. Every PNG here is
# derived from the master, so the .icns (a build-time artifact) stays fully
# regenerable from committed art — no opaque binary blob is committed.
#
# Usage: packaging/macos/generate-iconset.sh
# Requires: sips (macOS built-in). `magick` from the nix devshell also works.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ICONSET="$REPO_ROOT/packaging/icon/Visual Relay.iconset"
MASTER="$ICONSET/icon_512x512@2x.png"

if [[ ! -f "$MASTER" ]]; then
  echo "generate-iconset: master not found at $MASTER" >&2
  exit 1
fi

if ! command -v sips >/dev/null 2>&1; then
  echo "generate-iconset: sips not found (macOS built-in required)" >&2
  exit 127
fi

# name -> pixel size (the @2x master is left untouched at 1024).
gen() {
  local name="$1" size="$2"
  sips -z "$size" "$size" "$MASTER" --out "$ICONSET/$name" >/dev/null
  echo "  $name (${size}x${size})"
}

echo "generate-iconset: regenerating from $MASTER"
gen icon_16x16.png 16
gen icon_16x16@2x.png 32
gen icon_32x32.png 32
gen icon_32x32@2x.png 64
gen icon_128x128.png 128
gen icon_128x128@2x.png 256
gen icon_256x256.png 256
gen icon_256x256@2x.png 512
gen icon_512x512.png 512
# icon_512x512@2x.png IS the master — do not overwrite it.
echo "generate-iconset: done."
