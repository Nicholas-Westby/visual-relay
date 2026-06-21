# App Icon

The source artwork is `Visual Relay.iconset/icon_512x512@2x.png` (1024×1024 pixels).
Every other size is derived from this master, so all icon artifacts are
regenerable from committed source — no opaque binary blob is committed.

## Regenerate app-icon.ico (Windows)

```bash
magick "packaging/icon/Visual Relay.iconset/icon_512x512@2x.png" \
  -define icon:auto-resize=256,128,64,48,32,16 \
  src/VisualRelay.App/Assets/app-icon.ico
```

## Regenerate the runtime Dock PNG (macOS, dev + bare exec)

The app sets the macOS Dock tile at runtime from a committed PNG
(`src/VisualRelay.App/Assets/app-icon.png`, shipped as an `AvaloniaResource`).
NSImage cannot read the `.ico`, hence a dedicated PNG. Regenerate it from the
master with:

```bash
sips -z 512 512 "packaging/icon/Visual Relay.iconset/icon_512x512@2x.png" \
  --out src/VisualRelay.App/Assets/app-icon.png
```

## Regenerate the macOS .icns + VisualRelay.app bundle

The macOS Dock/Finder icon for the installed app comes from the bundle's
`Info.plist` (`CFBundleIconFile` → `Resources/VisualRelay.icns`). The full
`.iconset` (all 16…512 @1x/@2x sizes) is committed under
`Visual Relay.iconset/` and regenerated from the master with:

```bash
dotnet run --project tools/VisualRelay.Packaging/VisualRelay.Packaging.csproj -- generate-iconset
```

To build the bundle from a `dotnet publish` output for the macOS RID:

```bash
# Produces <output-dir>/VisualRelay.app/Contents/{Info.plist,MacOS/…,Resources/VisualRelay.icns}
dotnet run --project tools/VisualRelay.Packaging/VisualRelay.Packaging.csproj -- build-app-bundle <publish-dir> [output-dir]
```

`build-app-bundle` regenerates the iconset, runs
`iconutil -c icns "<iconset>" -o VisualRelay.icns`, lays out
`VisualRelay.app/Contents/{Info.plist, MacOS/<exe + payload>, Resources/VisualRelay.icns}`,
and writes a valid `Info.plist` (bundle id `org.minify.VisualRelay`, display
name "Visual Relay", `NSHighResolutionCapable`, a sane `LSMinimumSystemVersion`).
The macOS release tarball ships this bundle; the `visual-relay` launcher execs
its inner binary so the running process is associated with the `.icns`.
