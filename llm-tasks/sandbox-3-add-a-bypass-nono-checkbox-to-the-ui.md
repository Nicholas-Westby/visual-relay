# There's no way for a user to turn the nono sandbox off when they genuinely need unguarded access

`sandbox-1-run-swival-under-the-nono-guard-sandbox.md` gates the sandbox on a `bypassSandbox`
flag read from `.relay/config.json`, and `sandbox-2-...` makes nono required unless that flag is
set — but nothing in the app lets a user set it. The Avalonia app currently has **no checkbox or
toggle controls at all** (a grep for `CheckBox`/`ToggleSwitch`/`IsChecked` returns nothing);
boolean state is done with a `Button` bound to a `[RelayCommand]` that flips an
`[ObservableProperty]` (the Archive toggle: `ViewModels/MainWindowViewModel.cs:127-135`,
`Views/Controls/QueuePanel.axaml:30-34`). Config is **re-read per run** via
`RelayConfigLoader.LoadAsync(RootPath)` in `RunOneAsync` (`MainWindowViewModel.Execution.cs:182`),
and written by `RelayConfigWriter.Write` (`src/VisualRelay.Core/Init/RelayConfigWriter.cs`), which
today emits only a minimal `{ testCmd, testFileCmd }` object.

## Goal

Add a **"Bypass nono sandbox" checkbox** to the UI, **unchecked by default** (= sandbox on).
Toggling it persists `bypassSandbox` to `.relay/config.json` so the next run honours it, and the
control carries a clear warning that unchecking removes the delete-protection. This is the
*explicit user opt-out* — distinct from a missing-nono fallback, which `sandbox-2` forbids.

## Approach (suggested)

- **ViewModel**: add `[ObservableProperty] bool _bypassSandbox;` to `MainWindowViewModel`
  (consider a new `MainWindowViewModel.Settings.cs` partial to keep files under 300 lines).
  Hydrate it from `RelayConfigLoader.TryLoadAsync(RootPath)` wherever the VM refreshes for the
  current root, and on change **persist to `.relay/config.json`**.
- **Persistence**: extend `RelayConfigWriter` (or add a focused upsert helper) to write
  `bypassSandbox` while **preserving existing keys**. The current writer rebuilds a minimal
  object, so a naive call would clobber `tierProfiles`, `baselineVerify`, etc. — read-modify-write
  the existing JSON instead. Cover this with a test.
- **UI control**: introduce the app's first real `CheckBox` (clearest for a settings toggle):
  ```xml
  <CheckBox IsChecked="{Binding BypassSandbox}"
            Content="Bypass nono sandbox"
            ToolTip.Tip="Runs Swival without the OS sandbox. It will be able to delete files anywhere, not just in the project folder."/>
  ```
  Place it in a settings/run area of `QueuePanel.axaml` (or a small new settings control) with a
  short warning subtext. If you prefer to match the existing idiom instead of adding the first
  `CheckBox`, a `Button`+`[RelayCommand]` toggle mirroring `ToggleArchive`
  (`MainWindowViewModel.Commands.cs:111-117`) is acceptable — **state which** in the PR.
- Because config is re-read per run, no direct wiring to `SwivalSubagentRunner` is needed beyond
  persistence — `BuildArguments` already reads `_config.BypassSandbox` (from `sandbox-1`).

## Files

- `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` (or a new `.Settings.cs` partial).
- `src/VisualRelay.App/Views/Controls/QueuePanel.axaml` (the checkbox + warning).
- `src/VisualRelay.Core/Init/RelayConfigWriter.cs` (key-preserving upsert of `bypassSandbox`).
- Tests under `tests/VisualRelay.Tests/`.

## Tests (write the failing tests first)

- **RelayConfigWriter**: writing `bypassSandbox: true` produces valid JSON that
  `RelayConfigLoader` reads back as `BypassSandbox == true`, **and** preserves pre-existing keys
  (e.g. a config with `tierProfiles`/`baselineVerify` still has them afterward).
- **ViewModel**: `BypassSandbox` defaults to `false`; setting it persists to `.relay/config.json`
  and a subsequent `TryLoadAsync` reflects it. Use the existing Avalonia headless test pattern if
  a control-binding smoke test is added.

## Sequencing

Depends on `sandbox-1` (defines the `bypassSandbox` config key and reads it in `BuildArguments`).
Independent of `sandbox-2`.

## Done when

- A "Bypass nono sandbox" checkbox appears in the UI, unchecked by default, with a visible warning
  about losing delete-protection.
- Toggling it writes `bypassSandbox` to `.relay/config.json` (preserving other keys) and the next
  run honours it (sandbox on when unchecked, off when checked).
- The control hydrates from the persisted value when the app loads a root.
- `./visual-relay check` green; files under 300 lines; Conventional Commit subjects.

## Notes

- The checkbox is the **only** sanctioned no-nono path; it is not the missing-dependency fallback
  (`sandbox-2` hard-errors for that). Keep the warning copy unambiguous so users understand
  unchecking removes the accidental-delete protection.
- `RelayConfigWriter` currently overwrites with a minimal object — the key-preserving upsert is
  load-bearing; without it, toggling the checkbox would silently drop `tierProfiles` and other
  settings.
