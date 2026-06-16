# App Icon

The source artwork is `Visual Relay.iconset/icon_512x512@2x.png` (1024×1024 pixels).

## Regenerate app-icon.ico (Windows)

```bash
magick "packaging/icon/Visual Relay.iconset/icon_512x512@2x.png" \
  -define icon:auto-resize=256,128,64,48,32,16 \
  src/VisualRelay.App/Assets/app-icon.ico
```

## Future: macOS .icns

Not yet in scope — the app has no `.app` bundle or `Info.plist` today.
When a macOS bundle is added, generate the `.icns` from this same master with:

```bash
# Create a temporary .iconset with all required sizes, then:
iconutil -c icns <iconset-dir> -o <output>.icns
```
