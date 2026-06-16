# Make the Visual Relay app icon show on macOS (runtime Dock icon + real .app bundle)

The brand icon is correctly wired for **Windows** — `app-icon.ico` is referenced by the `.csproj`
`<ApplicationIcon>` (the `.exe` / Windows taskbar icon) and by `MainWindow.axaml` `Icon=` (the window
chrome). But on **macOS** the app shows the **generic .NET icon** in the Dock/taskbar, because:

- The macOS Dock tile is driven by the running process's `.app` **bundle** (`Info.plist`
  `CFBundleIconFile` → `.icns`), **not** by `<ApplicationIcon>` or Avalonia's `Window.Icon`; and macOS
  windows have no titlebar icon, so `Window.Icon` is invisible there too.
- The app is launched as a **bare executable** in *both* modes: dev is `dotnet run` (the process is
  `dotnet`), and the installed Homebrew build `exec`s `$PUBLISHED_APP` (`app/VisualRelay.App`, a bare
  Mach-O). Neither runs inside a `.app` bundle.

So the brand icon currently appears **nowhere** on macOS. `DONE-replace-app-icon` and
`packaging/icon/README.md` both explicitly **deferred** the macOS side ("Not yet in scope — the app
has no `.app` bundle or `Info.plist` today"). This task closes that gap.

This is an **app + packaging task** for Visual Relay's own macOS presentation — *not* a harness change.

## Goal (settled)

Do **both**, so the icon shows in every way the app is actually run:

1. **Runtime Dock icon** — set the Dock tile from the brand artwork at startup. This is what makes the
   icon appear during `./visual-relay launch` in dev (`dotnet run`) **and** for the bare published
   executable, neither of which is bundled.
2. **Real `.app` bundle** — produce `VisualRelay.app` (`Info.plist` + `.icns`) for proper installed /
   Finder presentation, and launch the published app **through** the bundle so macOS attaches the
   bundle icon.

## Current state (researched)

> **Freshness contract.** Verify every reference below by searching for the quoted string, not by line
> number; if a snippet has drifted, re-read the file and adapt.

- **Entry point:** `src/VisualRelay.App/Program.cs` → `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`.
  App lifecycle is `src/VisualRelay.App/App.axaml.cs` (`OnFrameworkInitializationCompleted`), which is
  the right place to set the Dock icon — AppKit's `NSApplication` is live by then.
- **Windows wiring (leave intact):** `src/VisualRelay.App/VisualRelay.App.csproj` —
  `<ApplicationIcon>Assets\app-icon.ico</ApplicationIcon>`; `src/VisualRelay.App/Views/MainWindow.axaml`
  — `Icon="/Assets/app-icon.ico"`. `Assets\**` is already included as `<AvaloniaResource>`, so anything
  dropped in `Assets/` is loadable at runtime via an `avares://` URI.
- **Master artwork:** `packaging/icon/Visual Relay.iconset/icon_512x512@2x.png` (1024×1024). The full
  macOS iconset (`icon_16x16.png` … `icon_512x512@2x.png`) is **not** committed — only this master is.
  (The complete set exists inside `llm-tasks/add-app-icon/Visual Relay Icon.zip` as a reference, but
  that task folder is retired with its run, so regenerate from the committed master instead of relying
  on the zip.) `packaging/icon/README.md` documents the `.ico` regen and defers `.icns`.
- **Launcher** (`./visual-relay`): `PUBLISHED_APP="$SCRIPT_DIR/app/VisualRelay.App"`; `HAS_PUBLISHED=1`
  when it is executable; `launch`/`run` does `exec "$PUBLISHED_APP" "$@"` when published, else
  `dotnet run --project "$SCRIPT_DIR/src/VisualRelay.App/VisualRelay.App.csproj" -- "$@"`.
- **Homebrew formula** `packaging/visual-relay.rb`: downloads `visual-relay-osx-{arm64,x64}.tar.gz`,
  `libexec.install Dir["*"]`, `bin.install_symlink libexec/"visual-relay"`. So the release tarball's
  layout is what `$SCRIPT_DIR` sees at runtime.
- **Tooling:** `iconutil` and `sips` are macOS built-ins; `magick` is available in the nix devshell
  (`flake.nix`). The release is built on macOS, so `iconutil` is available at package time.
- **Icon test conventions:** `tests/VisualRelay.Tests/AppIconTests.cs` shows the house style — assert
  file existence / parse headers / `Assert.Skip(...)` when an external tool isn't on `PATH`. Mirror it;
  don't force a test that needs AppKit to run in CI.

## What to build

### A. Runtime Dock icon (macOS only, best-effort)

On macOS only, set the Dock tile to the brand artwork once the app is initialized (e.g. from
`App.OnFrameworkInitializationCompleted`, after the desktop lifetime is set up). Avalonia 12.0.4
exposes no Dock-icon API, so use **AppKit interop**: P/Invoke the Objective-C runtime
(`/usr/lib/libobjc.dylib`: `objc_getClass`, `sel_registerName`, `objc_msgSend`) to call
`[[NSApplication sharedApplication] setApplicationIconImage: img]` where `img` is an `NSImage`
created from the brand PNG.

- Guard with `OperatingSystem.IsMacOS()`; a complete no-op on other platforms.
- **Best-effort:** wrap in try/catch and never let interop failure crash or block startup.
- Isolate it (e.g. a `MacDockIcon` helper class) so the interop is in one place.
- Load the image from a committed **PNG** (NSImage reads PNG cleanly; do not feed it the `.ico`). Add a
  reasonably sized PNG (e.g. 512×512 derived from the master) to `src/VisualRelay.App/Assets/`
  (e.g. `Assets/app-icon.png`) so it ships as an `AvaloniaResource`, and load it via its `avares://`
  URI (copy to a temp file for `NSImage initWithContentsOfFile:` if needed, or use
  `initWithData:`/`CGImage` — implementer's choice).

### B. Produce a real `VisualRelay.app` bundle (Info.plist + .icns)

Generate the `.icns` from the committed master and assemble a valid bundle:

- **`.icns`:** regenerate the full `.iconset` (all required sizes 16…512 @1x/@2x) from the committed
  1024² master with `sips`/`magick`, then `iconutil -c icns "<iconset>" -o VisualRelay.icns`. The
  `.icns` must be **regenerable from committed art** (commit the full iconset and/or a generation
  script — see §D); do not commit an unexplained binary blob with no source.
- **Bundle layout:** `VisualRelay.app/Contents/{Info.plist, MacOS/<exe + runtime payload>,
  Resources/VisualRelay.icns}`.
- **`Info.plist`** (valid plist): `CFBundleName`, `CFBundleDisplayName` = "Visual Relay";
  `CFBundleIdentifier` = `org.minify.VisualRelay` (Decision 3 — adjust only if you have a better
  reverse-DNS); `CFBundleExecutable` = the inner binary name; `CFBundleIconFile` = `VisualRelay`
  (the `.icns`, no extension); `CFBundlePackageType` = `APPL`; `CFBundleShortVersionString` /
  `CFBundleVersion` (wire to the formula's `0.1.0` or the build version); `NSHighResolutionCapable` =
  `true`; a sane `LSMinimumSystemVersion`.
- Assemble via a **committed packaging script** (e.g. `packaging/macos/build-app-bundle.sh`) that takes
  the `dotnet publish` output for the macOS RID and emits `VisualRelay.app`. This is the artifact the
  release tarball should contain.

### C. Launch through the bundle (installed path), keep runtime icon (dev path)

- **Launcher** (`./visual-relay`): when `HAS_PUBLISHED`, if a `VisualRelay.app` exists in the published
  payload, `exec` its inner binary (`…/VisualRelay.app/Contents/MacOS/<exe>`) so macOS associates the
  process with the bundle and shows the `.icns`. Preserve the current behavior when no bundle is
  present (fall back to the bare exec / `dotnet run`). Keep §A's runtime setter in place regardless —
  it covers the dev `dotnet run` path and is harmless belt-and-suspenders inside the bundle.
- **Homebrew formula** `packaging/visual-relay.rb`: ship the `.app` inside `libexec` and have the
  `visual-relay` CLI launch through it (per the launcher change above). Keep the `bin` symlink + CLI
  entry model; do not convert the formula into a Cask. Update the release-artifact layout/notes if the
  tarball contents change.

### D. Keep it regenerable + update the docs

Update `packaging/icon/README.md`: **remove** the "Future: macOS .icns — Not yet in scope" deferral and
document the real `.icns` + bundle generation (the `sips`/`iconutil` recipe and the bundle script), so
the macOS icon is rebuildable from the committed master.

## Tests / verification

Follow `AppIconTests.cs` conventions (existence / header / plist parse; `Assert.Skip` when a tool is
absent). Do not force a test that requires AppKit or a running Dock.

- **Runtime helper:** assert the Dock-icon helper is a safe **no-op off macOS** (returns false / does
  nothing) and that the brand PNG asset exists and loads as a bitmap. Guard any
  actually-calls-AppKit assertion behind `OperatingSystem.IsMacOS()` or skip it.
- **Bundle artifacts:** assert the generated `.icns` exists and starts with the `icns` magic; the
  `Info.plist` is a valid plist containing `CFBundleIconFile` = `VisualRelay` and a non-empty
  `CFBundleExecutable`; and the bundle has `Contents/MacOS` + `Contents/Resources`. If these are
  build-time outputs rather than committed, assert on the generator script instead (it exists, is
  executable, and references `iconutil`).
- **No Windows regression:** `Csproj_HasApplicationIcon`, `MainWindow_ReferencesAppIcon`,
  `IconSourceArtwork_*`, `IconReadme_*` still pass; the `.ico` wiring is untouched.
- **Build gate:** `./visual-relay check` green; app builds.
- **Manual (record evidence):** on macOS, launch via `./visual-relay launch` in dev (`dotnet run`,
  runtime setter) **and** via the built `VisualRelay.app`, and confirm the Dock shows the brand icon in
  both.

## Done when

- The brand icon shows in the macOS Dock/taskbar when the app runs **both** ways: dev
  `./visual-relay launch` (`dotnet run`) and the installed/published bundle.
- A valid `VisualRelay.app` (`Info.plist` + `Resources/VisualRelay.icns`) is produced by a committed,
  documented packaging step; the launcher and Homebrew formula launch through it.
- The `.icns` is regenerable from the committed 1024² master (full iconset → `iconutil`), documented in
  `packaging/icon/README.md` with the deferral note removed.
- The runtime Dock-icon code is macOS-guarded, best-effort (never crashes or blocks startup), and
  isolated.
- Windows wiring (`<ApplicationIcon>`, `MainWindow.axaml` `Icon=`, `app-icon.ico`) is untouched and
  still correct.
- `./visual-relay check` green; Conventional Commit subjects (e.g. `feat(app): set the macOS dock icon
  at runtime`, `feat(packaging): build a macOS .app bundle with .icns`, `feat(packaging): launch the
  published app through VisualRelay.app`).

## Decisions

These are **settled** — implement to them; no further design input is needed.

1. **Do both** the runtime Dock icon and the real `.app` bundle. *Why:* the runtime setter is the only
   thing that fixes the icon for the `dotnet run` dev workflow and the bare published exec; the bundle
   is the correct, durable macOS-citizen artifact for installed/Finder use. Each covers a gap the other
   doesn't.
2. **Runtime mechanism = AppKit interop** (`NSApplication setApplicationIconImage:` via objc P/Invoke),
   macOS-guarded and best-effort, loading a committed **PNG** asset — Avalonia 12.0.4 has no Dock API
   and `NSImage` won't take the `.ico` reliably.
3. **Bundle id `org.minify.VisualRelay`**, display name "Visual Relay", `CFBundleIconFile` = `VisualRelay`
   (the `.icns`). Adjust the identifier only if a better stable reverse-DNS is known; do not leave it
   unset.
4. **`.icns` from the committed master** via full iconset + `iconutil`; commit the regeneration recipe
   (script + README). The release tarball's `VisualRelay.app` contains the built `.icns`.
5. **Keep the Windows `.ico` wiring as-is.** This task adds macOS; it does not change or remove the
   existing `<ApplicationIcon>` / `Window.Icon`.

## Notes

- `NSImage` reads PNG, not `.ico` — use the master/512 PNG for the runtime Dock tile.
- Setting `applicationIconImage` must happen **after** AppKit/Avalonia is initialized (hence the
  lifecycle hook, not `Program.Main` before `StartWithClassicDesktopLifetime`).
- This unblocks the macOS deferral noted in `packaging/icon/README.md` and `DONE-replace-app-icon`.
- Don't expand scope into code-signing/notarization — that's a separate concern; this task is about the
  icon showing in the Dock and a well-formed bundle.
