# Replace the Visual Relay app icon with the new artwork

The current app icon (`src/VisualRelay.App/Assets/app-icon.ico`) does not look good. New, polished
icon artwork has been provided and staged in this task folder. Swap the app over to it and keep the
icon **regenerable** from source art in the repo.

This is an **app-asset task** for Visual Relay's own packaging — not a harness change. Being specific
to this app's icon wiring is correct; do not generalize it.

## Provided artwork (already in this task folder)

`llm-tasks/replace-app-icon/assets/` contains:

- `Visual Relay.iconset/` — the canonical macOS iconset: `icon_16x16.png` … `icon_512x512@2x.png`
  (the `icon_512x512@2x.png` is the 1024×1024 master).
- `app-icon.ico` — a ready, multi-frame Windows ICO (256/128/64/48/32/16 px) generated from that
  master with `magick … -define icon:auto-resize=256,128,64,48,32,16`. This is the artifact the app
  loads; you may copy it as-is or regenerate it (same command) to confirm it reproduces.

Use **this** artwork exactly — do not design or substitute a different icon.

## Current state (researched)

> **Freshness contract.** Verify these two references by searching for the quoted strings, not by
> line number; adapt if they have drifted.

The app icon is referenced in exactly two places, both pointing at the same file:

- `src/VisualRelay.App/VisualRelay.App.csproj` — `<ApplicationIcon>Assets\app-icon.ico</ApplicationIcon>`
  (the Windows executable icon).
- `src/VisualRelay.App/Views/MainWindow.axaml` — `Icon="/Assets/app-icon.ico"` (the in-app window
  icon).

There is **no** macOS `.app` bundle, `Info.plist`, or `.icns` in the repo today (packaging is the
Homebrew formula `packaging/visual-relay.rb`), so there is nothing else to rewire. `.icns` generation
is out of scope — note it for whoever adds a macOS bundle later, but do not add one here.

`magick` (ImageMagick) and `iconutil`/`sips` are available on this machine; the repo already depends
on ImageMagick for screenshots (see `08-harden-test-suite-against-hangs-and-missing-magick`).

## What to build

1. **Replace the icon.** Overwrite `src/VisualRelay.App/Assets/app-icon.ico` with the provided
   `llm-tasks/replace-app-icon/assets/app-icon.ico` (a valid multi-frame ICO, 6 frames 256→16). Keep
   the **same path and filename** so the two existing references keep resolving — do not rename the
   asset or edit the `.csproj` / `.axaml` references unless a reference has drifted.
2. **Keep it regenerable.** Add the source artwork to the repo so the icon is not an unexplained
   binary: place the iconset (or at least the 1024×1024 master `icon_512x512@2x.png`) under
   `packaging/icon/` (create it), and record the regeneration command in a short
   `packaging/icon/README.md`:
   `magick "packaging/icon/Visual Relay.iconset/icon_512x512@2x.png" -define icon:auto-resize=256,128,64,48,32,16 src/VisualRelay.App/Assets/app-icon.ico`.
   (If the iconset is judged too heavy for the repo, the single 1024 master is sufficient — keep at
   least that.)
3. **Clean up.** The `llm-tasks/replace-app-icon/assets/` staging copies are the delivery vehicle;
   the committed source of truth is what you place under `packaging/icon/`. Don't leave the artwork
   committed in two places — the task folder is retired with the run.

## Tests / verification

- `./visual-relay check` is green and the app **builds** with the new asset (the `<ApplicationIcon>`
  is consumed at build time — a malformed ICO fails the build, so a green build is the core
  regression gate).
- The replaced `src/VisualRelay.App/Assets/app-icon.ico` is a valid ICO: first bytes `00 00 01 00`
  and multiple frames (e.g. assert via `magick identify` in the run, or a tiny test reading the ICO
  header/frame count if an asset test fits the existing test conventions — do not force one if it
  doesn't).
- Both references still resolve to `Assets/app-icon.ico` (unchanged).
- Capture `./visual-relay screenshot` so the new window/title-bar icon can be eyeballed.

## Done when

- `src/VisualRelay.App/Assets/app-icon.ico` is the new artwork (multi-frame ICO from the provided
  master), loaded by both the `.csproj` `<ApplicationIcon>` and `MainWindow.axaml` `Icon=`.
- The source artwork + regeneration command live under `packaging/icon/` so the icon can be rebuilt.
- `./visual-relay check` green; app builds; Conventional Commit subject (e.g.
  `chore(app): replace app icon with new artwork`). No `.icns`/bundle added (noted as future work).

## Notes

- Do not touch unrelated assets or packaging beyond the icon.
- If `magick` is unavailable in the run environment, the provided `app-icon.ico` is already correct —
  copy it directly rather than failing the task.
