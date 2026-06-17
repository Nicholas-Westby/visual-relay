#!/usr/bin/env bash
# Assemble a macOS VisualRelay.app bundle from a `dotnet publish` output dir.
#
# This produces the durable macOS-citizen artifact: macOS reads the Dock/Finder
# icon from the bundle's Info.plist (CFBundleIconFile -> Resources/VisualRelay.icns),
# not from <ApplicationIcon> or Avalonia's Window.Icon. The release tarball ships
# this bundle and the visual-relay launcher execs its inner binary so the .icns
# is attached to the running process.
#
# The .icns is regenerated here from the committed iconset (which is itself
# regenerable from the master via generate-iconset.sh) — no opaque blob is
# committed.
#
# Usage:
#   packaging/macos/build-app-bundle.sh <publish-dir> [output-dir]
#
#   <publish-dir>  the `dotnet publish` output for the macOS RID, containing the
#                  inner executable (default name: VisualRelay.App) + payload.
#   [output-dir]   where to write VisualRelay.app (default: <publish-dir>/dist).
#                  The default is a subdir, not <publish-dir> itself: on macOS's
#                  case-insensitive filesystem a directory named VisualRelay.app
#                  collides with the bare VisualRelay.App exe in <publish-dir>.
#
# Requires: iconutil + sips (macOS built-ins).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

PUBLISH_DIR="${1:-}"
OUTPUT_DIR="${2:-${PUBLISH_DIR%/}/dist}"
EXE_NAME="${VISUAL_RELAY_APP_EXE:-VisualRelay.App}"
APP_NAME="VisualRelay.app"
ICON_NAME="VisualRelay"
BUNDLE_ID="org.minify.VisualRelay"
DISPLAY_NAME="Visual Relay"
SHORT_VERSION="${VISUAL_RELAY_VERSION:-0.1.0}"
BUNDLE_VERSION="${VISUAL_RELAY_BUNDLE_VERSION:-$SHORT_VERSION}"
MIN_MACOS="${VISUAL_RELAY_MIN_MACOS:-11.0}"

if [[ -z "$PUBLISH_DIR" || ! -d "$PUBLISH_DIR" ]]; then
  echo "build-app-bundle: publish dir not found: '$PUBLISH_DIR'" >&2
  echo "usage: $0 <publish-dir> [output-dir]" >&2
  exit 1
fi

if [[ ! -f "$PUBLISH_DIR/$EXE_NAME" ]]; then
  echo "build-app-bundle: inner executable '$EXE_NAME' not found in $PUBLISH_DIR" >&2
  echo "  override the name with VISUAL_RELAY_APP_EXE=<name>." >&2
  exit 1
fi

for tool in iconutil sips; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "build-app-bundle: $tool not found (macOS built-in required)" >&2
    exit 127
  fi
done

# 1. Regenerate the iconset from the master, then build the .icns.
"$SCRIPT_DIR/generate-iconset.sh"
ICONSET="$REPO_ROOT/packaging/icon/Visual Relay.iconset"
WORK_ICNS="$(mktemp -d)/$ICON_NAME.icns"
iconutil -c icns "$ICONSET" -o "$WORK_ICNS"

# 2. Lay out VisualRelay.app/Contents/{MacOS,Resources}.
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR_ABS="$(cd "$OUTPUT_DIR" && pwd)"
APP_DIR="$OUTPUT_DIR_ABS/$APP_NAME"
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

# Copy the entire publish payload under Contents/MacOS so the bundle is
# self-contained (the inner exe + its runtime live together). Skip the output
# dir itself when it is nested inside the publish dir (default: <publish>/dist)
# so we never copy the bundle into itself.
shopt -s dotglob
for entry in "$PUBLISH_DIR"/*; do
  [[ -e "$entry" ]] || continue
  entry_abs="$(cd "$(dirname "$entry")" && pwd)/$(basename "$entry")"
  [[ "$entry_abs" == "$OUTPUT_DIR_ABS" ]] && continue
  [[ "$(basename "$entry")" == "$APP_NAME" ]] && continue
  cp -R "$entry" "$APP_DIR/Contents/MacOS/"
done
shopt -u dotglob
chmod +x "$APP_DIR/Contents/MacOS/$EXE_NAME"

# 3. Place the .icns.
cp "$WORK_ICNS" "$APP_DIR/Contents/Resources/$ICON_NAME.icns"

# 4. Write Info.plist.
cat > "$APP_DIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$DISPLAY_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$DISPLAY_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key>
    <string>$EXE_NAME</string>
    <key>CFBundleIconFile</key>
    <string>$ICON_NAME</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$SHORT_VERSION</string>
    <key>CFBundleVersion</key>
    <string>$BUNDLE_VERSION</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSMinimumSystemVersion</key>
    <string>$MIN_MACOS</string>
</dict>
</plist>
PLIST

# Validate the plist if plutil is available (macOS built-in).
if command -v plutil >/dev/null 2>&1; then
  plutil -lint "$APP_DIR/Contents/Info.plist" >/dev/null
fi

echo "build-app-bundle: wrote $APP_DIR"
echo "  CFBundleExecutable=$EXE_NAME  CFBundleIconFile=$ICON_NAME  id=$BUNDLE_ID"
