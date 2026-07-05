# Allow Tasks to Skip Automated Testing

Add a per-task toggle — exactly paralleling the existing "10× turn budget" toggle — that lets a
task bypass the test-authoring requirement. When on, the **Author-tests** stage (stage 5) is
skipped: no new tests are authored, no red gate runs, no test-file/manifest merge or worktree
non-test-edit revert. The rest of the pipeline is unchanged — in particular stage 9 (Verify) still
runs the full mechanical test suite (tests "can still run") and still gates on regressions. Use
case: a README-only task that has no meaningful tests to write. Skipped stages must render visually
distinct (grayed out) on the stage board.

## Current state (researched)

The "10× turn budget" toggle is the precedent to mirror end-to-end:

- `src/VisualRelay.Domain/RelayConfig.cs` — record field `IReadOnlyList<string>? BoostTurnsTaskIds = null` (comment: "Task ids whose per-stage turn budget is multiplied by 10").
- `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` — default `BoostTurnsTaskIds: []` in `Defaults(...)`, and `BoostTurnsTaskIds = OptionalStringArray(root, "boostTurnsTaskIds", [])` in `TryLoadAsync(...)`.
- `src/VisualRelay.Core/Init/RelayConfigWriter.cs` — `public static void SetTurnBoost(string rootPath, string taskId, bool enabled)` read-modify-writes the `boostTurnsTaskIds` JSON array (de-dupes on add, removes on disable, preserves all other keys).
- Driver consumption: `RelayDriver.Invocation.cs` `var boosted = config.BoostTurnsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true;` (same pattern in `RelayDriver.VerifyFix.cs`).
- ViewModel: `src/VisualRelay.App/ViewModels/MainWindowViewModel.TurnBudget.cs` — `_boostedTaskIds` HashSet, `SelectedTaskBoostsTurns` get/set (setter persists via `RelayConfigWriter.SetTurnBoost`), `TurnBudgetLabel`, `CanToggleTurnBudget`, `HydrateTurnBudget(config)`. Hydration is called from `MainWindowViewModel.Helpers.cs` (`HydrateTurnBudget(configResult.Config)`). Rename migration calls `MigrateTrackingDictKey(_boostedTaskIds, oldId, newId)` in `MainWindowViewModel.TaskName.cs` `RekeyTaskId`. Selection-change re-raises the bound properties in `MainWindowViewModel.Commands.cs`.
- UI: `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` — a `CheckBox` bound to `SelectedTaskBoostsTurns` / `TurnBudgetLabel` / `CanToggleTurnBudget` (the only control in that header `Grid.Row="2"`).
- Control API: `src/VisualRelay.App/Services/ControlApi.cs` — `"boost-turns"` in the `PropertyActions` array, a `case "boost-turns":` that reads `{"value":bool}` and sets `viewModel.SelectedTaskBoostsTurns`; `ControlApi.State.cs` publishes `map["boost-turns"] = new { enabled = viewModel.SelectedTask is not null }`.

Stage model and the stage to skip:

- `src/VisualRelay.Core/Execution/RelayStages.cs` — `RelayStages.All` defines 11 stages; stage 5 is `Stage(5, "Author-tests", "balanced", "all", "all", ...)`.
- `src/VisualRelay.Core/Execution/RelayDriver.Stage5.cs` — `HandleStage5Async(...)` runs `WorktreeFilter.DiscardNonTestEditsAsync`, merges `testFiles` into the manifest, and runs the red gate via `AuthorTestGate.RunAsync`. Returns `Stage5Result(Outcome, Check, TestDurationSeconds)`. It already receives `taskId` and `config`.
- `src/VisualRelay.Core/Execution/RelayDriver.cs` — the stage loop calls `HandleStage5Async` for `stage.Number == 5`, then records the stage at the bottom of the loop via `RecordStageAsync(...)` (which sets status `"Done"` through `MarkStatusDone`). The existing skip precedent is stage 10: when Verify passes, the loop records stage 10 with body `"_Skipped: Verify passed; nothing to fix._"`, check `"green"`, sets `stage10Handled = true`, and the bottom-of-loop record is gated by `if (stage.Number != 9 || !stage10Handled)`.
- `src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs` — `MarkStatusDone(...)` sets `Status = "Done"`; `MarkStatus(entries, stageNumber, status)` is the low-level status setter. Add a sibling `MarkStatusSkipped` here.

Stage card styling (for the grayed-out look):

- `src/VisualRelay.App/ViewModels/StageRowViewModel.cs` — `AccentBrush`, `CardBackgroundBrush`, `BorderBrush`, `CardBorderThickness`, `CardShadow`, and `StatusLabel` are all `Status` switch expressions. Brushes already defined: `MutedBrush = "#7F8794"`, `WaitingCardBrush = "#171A20"`, `WaitingBorderBrush = "#2A303A"`, `NoShadow`. A `"Skipped"` case can reuse the muted/gray brush + no shadow to gray the card out.
- `src/VisualRelay.App/ViewModels/StageDetailViewModel.cs` already has a `StageDetailState.Skipped` enum value and `IsOutputSkipped`, and `StageOutputView.axaml` already renders a "This stage was skipped…" panel for it — reuse this for the skipped stage 5 detail view.

## What to build (TDD-first; mirror the boost-turns tests)

1. **Config field.** Add `IReadOnlyList<string>? SkipTestsTaskIds = null` to `RelayConfig` (next to `BoostTurnsTaskIds`). Wire it in `RelayConfigLoader`: default `SkipTestsTaskIds: []` in `Defaults(...)`, and `SkipTestsTaskIds = OptionalStringArray(root, "skipTestsTaskIds", [])` in `TryLoadAsync(...)`. Tests first: create `tests/VisualRelay.Tests/RelayConfigLoaderSkipTestsTaskIdsTests.cs` mirroring `RelayConfigLoaderBoostTurnsTaskIdsTests.cs` — absent → empty, present → values, non-array → empty.

2. **Config writer.** Add `public static void SetSkipTests(string rootPath, string taskId, bool enabled)` to `RelayConfigWriter`, copy-for-copy with `SetTurnBoost` but keyed on `skipTestsTaskIds`. Tests first: add `SetSkipTests_*` cases to `tests/VisualRelay.Tests/RelayConfigWriterTests.cs` mirroring the `SetTurnBoost_*` block (adds, idempotent, removes, preserves all other keys).

3. **Driver skip.** In `HandleStage5Async` (`RelayDriver.Stage5.cs`), when `config.SkipTestsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true`: append a ledger note (e.g. `"> **Skipped**: automated testing bypassed for this task."`), mark the stage 5 status entry `"Skipped"`, and return a `Stage5Result` that signals skipped (add a `Skipped` bool to the record struct) with `Check = "green"` and no test duration — without running the worktree filter, manifest merge, or red gate. Add `MarkStatusSkipped(entries, stage)` to `RelayDriver.Artifacts.cs` (next to `MarkStatusDone`). In `RelayDriver.cs`, mirror the stage-10 skip: introduce a `stage5Skipped` flag and gate the bottom-of-loop `RecordStageAsync` for stage 5 on `!stage5Skipped` (the skipped stage is recorded inside `HandleStage5Async` with body `"_Skipped: automated testing bypassed for this task._"`, check `"green"`). Stages 6–11 run unchanged; stage 9 still runs the full suite and gates normally. Tests first: add a driver test (pattern of `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs`, config JSON with `"skipTestsTaskIds": ["<taskId>"]`) asserting stage 5 is recorded with status `"Skipped"` and the run still proceeds through stage 9 to commit.

4. **ViewModel.** Add a new partial `src/VisualRelay.App/ViewModels/MainWindowViewModel.SkipTests.cs` (do not grow `MainWindowViewModel.TurnBudget.cs`): `_skipTestsTaskIds` HashSet, `SelectedTaskSkipsTests` get/set (setter persists via `RelayConfigWriter.SetSkipTests`), `SkipTestsLabel` (e.g. `"Skip automated testing"`), `CanToggleSkipTests`. Hydrate from config alongside `HydrateTurnBudget` (extend the call site in `MainWindowViewModel.Helpers.cs`). Add `MigrateTrackingDictKey(_skipTestsTaskIds, oldId, newId)` to `RekeyTaskId` in `MainWindowViewModel.TaskName.cs`. Re-raise the new properties on selection change in `MainWindowViewModel.Commands.cs` next to the existing boost re-raises. Tests first: add `SelectedTaskSkipsTests_*` cases to `tests/VisualRelay.Tests/MainWindowViewModelSettingsTests.cs` mirroring the `SelectedTaskBoostsTurns_*` block (hydrated from config, not-in-set, toggle persists both directions, defaults false).

5. **UI toggle.** In `TaskDetailPanel.axaml`, add a second `CheckBox` (next to the boost checkbox in the header) bound to `SelectedTaskSkipsTests` / `SkipTestsLabel` / `CanToggleSkipTests`, with a tooltip explaining it skips the Author-tests stage for docs/README-style tasks.

6. **Grayed-out stage card.** In `StageRowViewModel.cs`, add a `"Skipped"` case to `StatusLabel` (e.g. `"Skipped"`), `AccentBrush` (`MutedBrush`), `CardBackgroundBrush` (`WaitingCardBrush`), `BorderBrush` (`WaitingBorderBrush`), `CardBorderThickness` (`new Thickness(1)`), and `CardShadow` (`NoShadow`) so a skipped card reads muted/gray with no glow. Ensure `StageDetailViewModel`/`StageOutputView` show the existing skipped panel for a skipped stage 5 (reuse `StageDetailState.Skipped` / `IsOutputSkipped`).

7. **Control API.** Add `"skip-tests"` to the `PropertyActions` array in `ControlApi.cs`, add a `case "skip-tests":` mirroring `case "boost-turns":` (reads `{"value":bool}`, sets `viewModel.SelectedTaskSkipsTests`), and publish `map["skip-tests"] = new { enabled = viewModel.SelectedTask is not null }` in `ControlApi.State.cs`. Tests first: add `"skip-tests"` to the allowlist assertion in `tests/VisualRelay.Tests/ControlApiTests.cs` and add a toggle test mirroring the boost-turns one.

## Done when

- A task whose id is in `skipTestsTaskIds` (toggled via the UI checkbox, the `skip-tests` control-API command, or hand-edited config) runs the pipeline with stage 5 recorded as `"Skipped"` and never invokes `WorktreeFilter`, `AuthorTestGate`, or the test-file manifest merge.
- Stages 6–11 run normally; stage 9 still executes the full test suite and can still flag on regressions.
- The skipped stage-5 card on the stage board is visibly grayed out (muted accent, no shadow), and its detail view shows the "skipped" panel.
- The toggle persists to `.relay/config.json` under `skipTestsTaskIds`, survives reload, and re-keys on task rename.
- `./visual-relay check` passes: file-size guard (every C#/XAML file ≤ 300 lines — see note), format verification, build, the full test suite, and the README screenshot render.

## Guardrails

- Conventional Commits only (the `commit-msg` hook enforces the full ruleset: fixed type set, ≤72-char subject, lowercase after prefix, no trailing period, no em dashes, ≤3 `- ` body bullets ≤20 words each). See `docs/commit-messages.md` and `AGENTS.md`.
- Run the full gate before considering done: `./visual-relay check`.
- C# and Avalonia XAML source files must stay under 300 lines (`tools/VisualRelay.Guards`, run by `./visual-relay check`). **Two files are already at the 300-line ceiling** — keep their net change at ≤ 0 lines each: `src/VisualRelay.Core/Execution/RelayDriver.cs` (extract a small existing inline block into a partial so the `stage5Skipped` gating does not push it over) and `src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs` (the skip-tests selection re-raises must share the existing `OnPropertyChanged` block with no new lines, or move into the new partial). Put skip-VM logic in a new `MainWindowViewModel.SkipTests.cs` partial rather than growing `MainWindowViewModel.TurnBudget.cs`.
- Headless UI tests use `[AvaloniaFact]`/`[AvaloniaTheory]`; `HeadlessUnitTestSession` is banned (BannedApiAnalyzers — reintroducing it fails the build). Plain logic tests use xUnit `[Fact]`/`[Theory]` with the `TestRepository` helper, matching the existing boost-turns tests.
- Minimal diffs: change only what this task needs; do not reformat or reflow unrelated code. Retrofitting the existing stage-10 skip to the new `"Skipped"` visual is out of scope.
