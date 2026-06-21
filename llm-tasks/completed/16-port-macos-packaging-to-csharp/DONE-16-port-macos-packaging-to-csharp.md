# Port the macOS packaging scripts to C#

`packaging/macos/build-app-bundle.sh` (127) assembles `VisualRelay.app` from a `dotnet
publish` dir (lay out `Contents/{MacOS,Resources}`, build the `.icns`, write `Info.plist`),
and `packaging/macos/generate-iconset.sh` (45) regenerates the `.iconset` from the 1024px
master via `sips`. Move the orchestration into tested C# and leave the scripts as thin
wrappers (or retire them).

This design is decided — implement exactly this, no alternatives:

- New **`tools/VisualRelay.Packaging`** (Exe, `net10.0`) with subcommands
  `build-app-bundle <publish-dir> [output-dir]` and `generate-iconset`. The orchestration
  (bundle layout, `Info.plist` content, iconset size table) lives in C#.
- C# **may shell out to the macOS-native tools** (`sips`, `iconutil`, `plutil`) — there is no
  managed replacement and reimplementing them is out of scope — but it must not carry big bash
  logic, and should use native .NET for everything else (file copy/layout, writing the plist
  XML, the size loop). This matches the project rule: terminal calls are fine; bunches of
  logic in them are not.
- The two scripts become **thin wrappers** that `exec` the tool (≤ 20 logic lines), or are
  retired if nothing else calls them — `release.yml` calls the tool directly.

> **Sequencing — task 5 of 6 (12 → 17).** Independent of 13–15; can land any time before 17.

## Current state (researched)

- **`build-app-bundle.sh`**: validates the publish dir + inner exe; requires `iconutil`+`sips`;
  calls `generate-iconset.sh`, then `iconutil -c icns`; lays out `VisualRelay.app/Contents/
  {MacOS,Resources}` (copying the whole publish payload, skipping the nested output dir);
  writes `Info.plist` from a heredoc (`build-app-bundle.sh:92-119`) with
  `CFBundleIdentifier=org.minify.VisualRelay`, `CFBundleExecutable`/`CFBundleIconFile`,
  versions from `VISUAL_RELAY_VERSION`/`…_BUNDLE_VERSION`, `LSMinimumSystemVersion` from
  `VISUAL_RELAY_MIN_MACOS`; `plutil -lint`s it. Overridable via `VISUAL_RELAY_APP_EXE` etc.
- **`generate-iconset.sh`**: from `packaging/icon/Visual Relay.iconset/icon_512x512@2x.png`
  (the 1024px master), `sips -z`-resizes into the 9 standard iconset PNGs (16…512, @1x/@2x),
  leaving the master untouched.
- **Callers:** `.github/workflows/release.yml` (the only workflow; macOS build job) assembles
  the release layout and ad-hoc-signs (`codesign --force -s -`); it invokes these scripts. No
  other repo caller; `visual-relay` does not call them.
- **Characterization tests:** `MacAppBundleTests`, `MacDockIconTests`, `AppIconTests` assert
  the bundle/plist/icon outcomes — keep them green (re-point to the C# tool's output).
- **Tool convention:** mirror `tools/VisualRelay.Init` csproj + top-level `Program.cs`;
  register in `VisualRelay.slnx`. These run only in CI packaging (SDK present) and on macOS.

## What to build

TDD — write the failing tests first.

1. **`tools/VisualRelay.Packaging`** with two commands:
   - **`generate-iconset`**: resolve the iconset dir + master; for each `(name,size)` in the
     size table, `sips -z size size master --out name` (shell out to `sips` only); never
     overwrite the master. A guard if `sips`/master is missing (clear stderr + exit code).
   - **`build-app-bundle <publish-dir> [output-dir]`**: validate inputs + presence of
     `iconutil`/`sips`; call the iconset generation; `iconutil -c icns`; create the bundle
     layout and copy the publish payload (native .NET file APIs, skipping the nested output dir
     and the `.app` itself); write `Info.plist` (build the XML in C#, honoring the same
     env/version knobs); `plutil -lint`. Print the written path.
2. **Make the scripts thin wrappers** that `exec` the tool (keep the shebang +
   `set -euo pipefail` + the `exec` line), or delete them and update `release.yml` to call the
   tool — pick whichever leaves no orphaned script. Update `release.yml` accordingly.
3. **Tests:** unit-test the plist builder (identifiers/versions/min-macOS) and the iconset size
   table; an integration test that, on macOS, runs `build-app-bundle` against a tiny fake
   publish dir and asserts the bundle layout + a lint-clean plist (skip/guard the `sips`/
   `iconutil` parts on non-macOS). Re-point `MacAppBundleTests`/`MacDockIconTests`/`AppIconTests`.

## Done when

- `VisualRelay.Packaging build-app-bundle`/`generate-iconset` reproduce today's bundle and
  iconset (layout, `.icns`, lint-clean `Info.plist` with the same identifiers/versions),
  verified by tests that fail against the (absent) C# tool.
- The two `.sh` files are thin wrappers (≤ 20 logic lines, passing `./visual-relay guards`) or
  deleted; `release.yml` invokes the C# tool and the release bundle is unchanged; the
  packaging characterization tests are green.
- `./visual-relay check` is green; changed C# files < 300 lines; Conventional Commit subject
  e.g. `refactor(packaging): port macOS app-bundle and iconset scripts to C#`.
- Coordination: only `release.yml` references these; update it in this task. Independent of the
  Cli/backend/guard tasks.
