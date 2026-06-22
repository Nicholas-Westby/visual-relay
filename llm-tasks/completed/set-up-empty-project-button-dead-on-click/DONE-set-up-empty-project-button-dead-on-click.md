# "Set up empty project" is permanently disabled (stale `CanExecute`) — clicking does nothing

In the empty-state "Initialize this project" card, the **"Set up empty project"** button never
responds to clicks, so an empty folder can't be bootstrapped from the GUI. **Root cause confirmed by
live instrumentation** (this is a real, fixable, headless-testable bug — not the live-only mystery an
earlier draft of this task assumed).

## Root cause (confirmed — bake in)

The button's `CanExecute` is **frozen at its startup value**:

- The button binds `Command="{Binding BootstrapProjectCommand}"`. Its gate is
  `CanBootstrapProject() => !IsBusy && Directory.Exists(RootPath)`
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Bootstrap.cs`).
- In `MainWindowViewModel.cs`, **neither** `_rootPath` **nor** `_isBusy` declares
  `[NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))]`, and **nothing anywhere** calls
  `BootstrapProjectCommand.NotifyCanExecuteChanged()` (verified by grep). `_rootPath` only has
  `[NotifyPropertyChangedFor]` for RootName/RootParentPath/WindowTitle; `_isBusy` notifies
  CreateConfig/AddAttachments but not Bootstrap.
- So `CanExecute` is evaluated **once**, when the command is first created at startup — when
  `RootPath` is still empty → `Directory.Exists("")` = false → the button is created **disabled**.
  Opening/choosing a folder later sets `RootPath`, but the command is never re-queried, so the button
  **stays disabled for the life of the process**.
- A disabled Avalonia control does not receive pointer input — the press falls through to the
  element behind it — so clicks silently do nothing. The base theme dims disabled buttons, so it
  **correctly looks disabled** (user-confirmed).

### Why earlier checks said it was "enabled" (don't be fooled again)

- The localhost control API (`/state.commands.bootstrap.enabled` and `/command/bootstrap`) calls
  `command.CanExecute(null)` **live**, so it returns `true` even while the *button* holds a stale
  `false`. Live API "enabled" ≠ the button being clickable.
- The headless test `ConfigInitEmptyStateUiTests` sets `RootPath` at VM **construction** (before the
  button binds), so `CanExecute` was already `true` when first evaluated — hiding the bug. The live
  app sets `RootPath` **after** the window/button exist (via `LoadInitialAsync` / folder open).

### Evidence captured live (instrumentation, since reverted)

- Pointer log on the button: `btnEffEnabled=False`; press `source=Border` (falls through to the card
  border behind), button's own `PointerPressed`/`Click` never fire. Sibling "Find it for me"
  (`source=AccessText`) does receive input. `RenderScaling=1` (not a DPI issue).
- Same moment, `/state` reported `bootstrap.enabled: true` → the live-vs-cached desync.

## Fix

Add `[NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))]` to **both** `_rootPath` and
`_isBusy` in `MainWindowViewModel.cs` (alongside their existing notify attributes). That re-raises
`CanExecuteChanged` when the folder or busy-state changes, so the button enables as soon as
`RootPath` points at an existing directory.

> **Freshness contract.** Confirm by searching for `private string _rootPath`, `private bool _isBusy`,
> and `CanBootstrapProject`; place the attribute with the other `[Notify…]` attributes on each field.

Notes:
- `Directory.Exists(RootPath)` is a filesystem check, not a reactive property — re-evaluation is
  driven by the `RootPath` **string** changing (which happens on folder-open) and by `IsBusy`. That
  covers the real flow. After a successful bootstrap, `RefreshAsync` already runs.
- **Audit sibling risk:** check any other command whose `CanExecute` reads `RootPath` /
  `Directory.Exists` / other non-`[ObservableProperty]` state for the same frozen-`CanExecute` class.

## Test (TDD — write first; it must reproduce the bug, then pass)

Headless, in the style of `tests/VisualRelay.Tests/ConfigInitEmptyStateUiTests.cs`, but **mirror the
real init order** (this is the crux — setting `RootPath` at construction hides the bug):

1. `new MainWindowViewModel()` with **no** `RootPath` (default/empty).
2. Show `MainWindow`, `RunJobs` — the bootstrap button binds while `RootPath` is empty.
3. **Then** set `vm.RootPath = repo.Root` (an existing dir; ensure `NeedsInitialization` so the card
   is realized), `RunJobs`.
4. Assert the bootstrap button's `IsEffectivelyEnabled` is `true` (and/or a real
   `MouseDown`/`MouseUp` at its center fires `BootstrapProjectCommand`).

This **fails today** (button stuck disabled) and **passes** once the notify attributes are added.
Keep `ConfigInitEmptyStateUiTests` green.

## Out of scope

- The truncated labels on the other two buttons (`init-panel-buttons-labels-clipped`).
- A custom `Button:disabled` style — the disabled look is correct here; the fix is to not *be*
  disabled. (If desired, distinguishing disabled styling is a separate polish task.)

## Screenshot

- `init-panel-cropped.png` — the card with the (disabled, unresponsive) "Set up empty project".
