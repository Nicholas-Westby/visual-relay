# Add a per-task "10× turn budget" toggle for unusually large tasks

Some tasks are far bigger than normal and exhaust the default per-stage turn budget. Rather
than exposing a raw turn-count field, add a per-task toggle that multiplies the default turn
budget by **10×** for that one task. The default is unchanged (the configured `maxTurns`,
200), and the control must make its effect obvious to end users (show the resulting number,
not just "10×").

> **Implementation order:** task **02** of a batch. It shares the per-repo settings pattern
> with `01-settings-cog-opt-out-of-committing-relay-proof` (a `RelayConfig` field + a
> `RelayConfigLoader` parse + a `RelayConfigWriter` upsert + hydration at
> `MainWindowViewModel.Helpers.cs:101-104`). If task 01 is already implemented, add this
> field **alongside** its `commitProofArtifacts` field and **extend** the existing hydrate
> code rather than re-plumbing it.

## Current state (researched)

### How the turn budget works today
- The budget is `RelayConfig.MaxTurns` (default **200**) — `src/VisualRelay.Domain/RelayConfig.cs:11`,
  `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs:24,132` (`maxTurns`).
- It is applied **per stage invocation**. The single construction site is
  `RelayDriver.BuildInvocation`, which passes `config.MaxTurns` into
  `new StageInvocation(…)` (`src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs:244,256`).
  `StageInvocation.MaxTurns` (`src/VisualRelay.Domain/StageInvocation.cs:15`) is consumed by
  the subagent runner as `--max-turns` (`src/VisualRelay.Core/Execution/ProcessRunners.cs:60`).
- `config.MaxTurns` has exactly **one** consumer — that builder — so it is the single place
  to apply a multiplier. `BuildInvocation` already has both `taskId` and `config` in scope
  (`VerifyFix.cs:232,234`), so a per-task decision needs no new plumbing into the driver.

### Per-task identity + settings store
- Tasks are `RelayTaskItem` records identified by `Id`
  (`src/VisualRelay.Domain/RelayTaskItem.cs`), surfaced as `TaskRowViewModel.Id` and the
  window VM's `SelectedTask` (`src/VisualRelay.App/ViewModels/TaskRowViewModel.cs:37`;
  bound in `QueuePanel.axaml:70` and `TaskDetailPanel.axaml`).
- There is no per-task settings store yet. Per-repo settings live in `.relay/config.json`
  and follow the `BypassSandbox` pattern: a `RelayConfig` field, an `OptionalBool`/array
  parse in `RelayConfigLoader`, a read-modify-write `RelayConfigWriter.Upsert…` that
  preserves other keys (`src/VisualRelay.Core/Init/RelayConfigWriter.cs:45-66`), an
  `[ObservableProperty]` with an `OnChanged` upsert
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Settings.cs`), and hydration from the
  loaded config (`MainWindowViewModel.Helpers.cs:101-104`). `RelayConfigLoader` already has
  an `OptionalStringArray` helper (`RelayConfigLoader.cs:200-205`).
- The TaskDetailPanel header right cluster holds the per-task Run/Resume controls and chips
  (`src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml:19-45`) — the natural home for
  a per-task, run-affecting setting.

## What to build

TDD — write the failing test first.

### 1. Config plumbing (store the boosted task ids per repo)
- Add `IReadOnlyList<string> BoostTurnsTaskIds` to `RelayConfig` (trailing optional; default
  empty). Parse `boostTurnsTaskIds` via the existing `OptionalStringArray` in
  `RelayConfigLoader.TryLoadAsync`; `Defaults(...)` yields an empty list.
- Add `RelayConfigWriter.SetTurnBoost(rootPath, taskId, bool enabled)`: read-modify-write the
  `boostTurnsTaskIds` array (add or remove `taskId`, de-duplicated), preserving every other
  key — mirror `UpsertBypassSandbox`.

### 2. Apply the multiplier at the single choke point (`VerifyFix.cs:256`)
- Introduce a documented `private const int TurnBoostMultiplier = 10;`.
- In `BuildInvocation`, compute
  `var turns = config.BoostTurnsTaskIds.Contains(taskId, StringComparer.Ordinal) ? config.MaxTurns * TurnBoostMultiplier : config.MaxTurns;`
  and pass `turns` to `StageInvocation` instead of `config.MaxTurns`. This boosts **every
  stage** of that task (matching how `MaxTurns` already applies), and leaves non-boosted
  tasks untouched.

### 3. UI: a clearly-labeled per-task toggle
- Add a `CheckBox` for the selected task in the TaskDetailPanel header (or a thin row just
  beneath it) bound to a new VM property `SelectedTaskBoostsTurns`. **The label must state
  the effect**, computed from config — e.g. `10× turn budget ({MaxTurns} → {MaxTurns*10})`
  ("10× turn budget (200 → 2000)") via a `TurnBudgetLabel` computed string, with a tooltip:
  "Use for unusually large tasks. Multiplies the default per-stage turn limit by 10 for this
  task only."
- VM (put this in a **new** partial `MainWindowViewModel.TurnBudget.cs` — **not**
  `MainWindowViewModel.Layout.cs`, which `04-collapsible-panels-and-task-focus-mode` owns):
  - Hold the loaded `boostTurnsTaskIds` set; hydrate it from config on load alongside
    `BypassSandbox` (`Helpers.cs:101-104`).
  - `SelectedTaskBoostsTurns`: getter = set contains `SelectedTask?.Id`; setter = add/remove
    the id, call `RelayConfigWriter.SetTurnBoost(RootPath, SelectedTask.Id, value)`, and
    raise change notification. Re-raise `SelectedTaskBoostsTurns`/`TurnBudgetLabel` when
    `SelectedTask` changes.
  - Disable/hide the toggle when no task is selected or the repo isn't initialized.
- Optional (keep only if cheap): a small `10×` badge on boosted cards in `QueuePanel`'s item
  template (via a `TaskRowViewModel` flag) so boosted tasks are visible at a glance.

## Done when
- A per-task toggle, labeled with the resulting count (e.g. "10× turn budget (200 → 2000)"),
  appears for the selected task; toggling it persists/removes the task id under
  `boostTurnsTaskIds` in `.relay/config.json` (other keys preserved) and reflects the saved
  state when the repo or task is reselected.
- When a boosted task runs, every stage invocation uses `MaxTurns × 10` — the subagent
  `--max-turns` reflects the boosted number (`ProcessRunners.cs:60`); a non-boosted task is
  unchanged at `MaxTurns`.
- The multiplier is a fixed, documented `10` — there is no free-form turn-count entry (per
  the requirement).
- Tests first: `RelayConfigLoader` round-trips `boostTurnsTaskIds` (absent → empty);
  `SetTurnBoost` adds then removes an id while preserving other keys; `BuildInvocation`
  produces `MaxTurns*10` for an id in the set and `MaxTurns` otherwise (assert via the
  captured `StageInvocation.MaxTurns`); a VM test that `SelectedTaskBoostsTurns` and
  `TurnBudgetLabel` read and mutate the set correctly as `SelectedTask` changes.
- `./visual-relay check` green; changed files < 300 lines; compiled bindings clean;
  Conventional Commit subjects (e.g. `feat(run): add per-task 10× turn-budget toggle`).
