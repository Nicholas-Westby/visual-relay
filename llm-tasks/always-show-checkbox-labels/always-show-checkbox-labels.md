# Always Show Checkbox Labels

The two per-task toggles in the task-detail header — "10× turn budget" and "Skip automated testing" — render their labels via `TurnBudgetLabel` / `SkipTestsLabel`, which return `string.Empty` whenever no task is selected or the repo isn't initialized. The `CheckBox`es themselves have no `IsVisible` binding, so in those states (e.g. authoring the first task in an empty repo) the boxes appear with blank labels. Fix: labels always return their text; when the toggles shouldn't apply, hide the **whole** `CheckBox` via visibility, never by blanking the label.

## Current state (researched)

- `src/VisualRelay.App/ViewModels/MainWindowViewModel.TurnBudget.cs` —
  `public string TurnBudgetLabel => SelectedTask is not null && !string.IsNullOrEmpty(RootPath) ? $"10× turn budget ({_maxTurns} → {_maxTurns * 10})" : string.Empty;`
  (`_maxTurns` defaults to `200`, so the text form is deterministic even before config loads.)
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.SkipTests.cs` —
  `public string SkipTestsLabel => SelectedTask is not null && !string.IsNullOrEmpty(RootPath) ? "Skip automated testing" : string.Empty;`
- Both files also define the matching `CanToggleTurnBudget` / `CanToggleSkipTests` =>
  `SelectedTask is not null && !string.IsNullOrEmpty(RootPath) && !IsBusy` (used for `IsEnabled`, **not** visibility — busy must keep the box visible-but-disabled).
- `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` — the two `CheckBox`es (`Grid.Row="2"` bound to `SelectedTaskBoostsTurns`/`TurnBudgetLabel`/`CanToggleTurnBudget`, and `Grid.Row="3"` bound to `SelectedTaskSkipsTests`/`SkipTestsLabel`/`CanToggleSkipTests`) have **no `IsVisible` binding**. This file is 299 lines — at the repo's 300-line cap, so the `IsVisible` attributes must go inline on the existing `CheckBox` tags (no net new lines).
- The labels are re-raised on selection change in `src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs`:
  `OnPropertyChanged(nameof(TurnBudgetLabel)); OnPropertyChanged(nameof(SkipTestsLabel));`
  (next to the `CanToggle*` re-raises), and in `HydrateTurnBudget` / `HydrateSkipTests`.
- Tests live in `tests/VisualRelay.Tests/MainWindowViewModelSettingsTests.cs` (xUnit `[Fact]`). `TurnBudgetLabel_shows_calculated_numbers` currently asserts `Assert.Equal(string.Empty, viewModel.TurnBudgetLabel)` with no selection — this assertion must change.

## What to build (TDD-first)

1. **Tests first** in `MainWindowViewModelSettingsTests.cs`:
   - Update `TurnBudgetLabel_shows_calculated_numbers`: with a bare `new MainWindowViewModel()` (no selection, no root), assert `TurnBudgetLabel == "10× turn budget (200 → 2000)"` (no longer empty) and `Assert.False(viewModel.AreTaskTogglesVisible)`.
   - Add `SkipTestsLabel_always_shows_text`: bare viewmodel → `SkipTestsLabel == "Skip automated testing"` and `Assert.False(viewModel.AreTaskTogglesVisible)`.
   - In one existing hydrated test that has a selected task (e.g. `SelectedTaskBoostsTurns_hydrated_from_config_on_load`), also assert `Assert.True(viewModel.AreTaskTogglesVisible)`.
2. **Labels always return text.** In `MainWindowViewModel.TurnBudget.cs` drop the `?: string.Empty` branch so `TurnBudgetLabel` always returns `$"10× turn budget ({_maxTurns} → {_maxTurns * 10})"`. In `MainWindowViewModel.SkipTests.cs` drop the branch so `SkipTestsLabel` always returns `"Skip automated testing"`.
3. **Add one shared visibility property** (both toggles share the identical condition, so a single property avoids duplication). In `MainWindowViewModel.TurnBudget.cs` next to `CanToggleTurnBudget`:
   `public bool AreTaskTogglesVisible => SelectedTask is not null && !string.IsNullOrEmpty(RootPath);`
   Re-raise `nameof(AreTaskTogglesVisible)` at every site that currently raises `TurnBudgetLabel`/`SkipTestsLabel`: the selection-change block in `MainWindowViewModel.Commands.cs`, `HydrateTurnBudget`, and `HydrateSkipTests`.
4. **Bind visibility in XAML.** In `TaskDetailPanel.axaml`, add `IsVisible="{Binding AreTaskTogglesVisible}"` inline to each of the two existing `CheckBox` tags (do not add new elements/lines — the file is at the 300-line cap).
5. Run `./visual-relay check`.

## Done when

- With no task selected (e.g. empty repo / new-task dialog with no prior selection), both `CheckBox`es are hidden — no label-less boxes render.
- With a task selected, both `CheckBox`es are visible with their full labels (`10× turn budget (200 → 2000)` and `Skip automated testing`); while busy they remain visible but disabled (via the existing `CanToggle*` `IsEnabled` binding).
- `TurnBudgetLabel` and `SkipTestsLabel` never return `string.Empty`.
- `./visual-relay check` passes (file-size guard, format, build, tests, screenshot render).

## Guardrails

- Build/test/gate through the single entry point: `./visual-relay build`, `./visual-relay test`, `./visual-relay check` (file-size guard + format + build + tests + screenshot). Filter to the settings tests during dev: `./visual-relay test MainWindowViewModelSettingsTests`.
- These are non-UI ViewModel unit tests — keep using xUnit `[Fact]` (not `[AvaloniaFact]`).
- C# and Avalonia XAML sources must stay under 300 lines (`TaskDetailPanel.axaml` is at 299 — fold `IsVisible` into existing tags, no net new lines).
- Commit directly on `main` with a Conventional Commit subject: fixed type set, ≤72-char subject, lowercase after the prefix, no trailing period, no em dashes, body ≤3 `- ` bullets (≤20 words each). See `docs/commit-messages.md`.
- Minimal diff: change only the two partial ViewModel files, the axaml, and the test file. Do not reformat unrelated code.
