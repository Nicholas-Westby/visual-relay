# Queue-row elapsed shows the current stage's time, not the overall task time

## Problem

In a "Run All" drain, the LEFT queue list's running-task row reads e.g.
`Stage 05 · Author-tests · 4m 49s`, but that `4m 49s` is the **execute-phase** elapsed —
which, because execute begins right at stage 5, numerically tracks the *current stage's*
elapsed — not the **overall** wall-clock since the task began its pipeline. The task had
actually been running ~10 min (Ideate 15s, Research 34s, Diagnose 55s, Plan 11s — the parallel
planning stages 1–4 — then Author-tests ~5 min, plus inter-stage gaps), yet the queue row shows
only ~4m49s. It also sits a few seconds out of phase with the STAGES card's own `Running 4m 46s`
(49s vs 46s). EXPECTED: the queue-row time is the overall task elapsed (wall-clock since the
task's pipeline began, planning stages included), ticking in sync.

## Current state (researched 2026-06-26 — re-grep anchors)

- The queue row renders `TaskRowViewModel.MetricsLine` (`src/VisualRelay.App/ViewModels/TaskRowViewModel.cs`),
  running branch → `$"{RunningStepLabel} · {_runningElapsedLabel}"`; bound at
  `QueuePanel.axaml` `Text="{Binding MetricsLine}"`.
- `_runningElapsedLabel` is written ONLY by `MainWindowViewModel.UpdateRunningElapsedLabels()`
  (`MainWindowViewModel.LiveState.cs`) as `ElapsedFormatter.Label(now - _runStartedAt[taskId])`.
- `_runStartedAt` has a **single writer**: `BeginRunningTask` (`LiveState.cs`,
  `_runStartedAt[id] = DateTimeOffset.UtcNow`); removed in `ClearRunningTask`, migrated in
  `MainWindowViewModel.TaskName.cs`. `BeginRunningTask` is called from exactly two places: the
  drain's `OnExecuteStarted` callback (`CreateDrainLifecycleCallbacks`, `LiveState.cs`) and the
  single-run `RunOneAsync` (`MainWindowViewModel.Execution.cs`).
- **Root cause (drain only):** `RelayQueueController.DrainAsync` runs **Phase 1 = parallel
  planning of stages 1–4** (Ideate/Research/Diagnose/Plan via `PlanPhaseRunner`; `RelayStages.All`
  numbers stage 5 = Author-tests) and fires `OnPlanningStarted` there, then **Phase 2 = serial
  execute** firing `OnExecuteStarted` (`RelayQueueController.cs` ~199). The drain's
  `OnPlanningStarted` callback only calls `MarkPlanning()` — it captures no start time. So
  `_runStartedAt` is first set at `OnExecuteStarted → BeginRunningTask`, i.e. **execute-start**,
  which begins at stage 5. The queue-row elapsed therefore measures only the execute phase and
  tracks the Author-tests stage; planning stages 1–4 and any queue wait are excluded.
- **Why the ~3s phase offset (NOT two timers):** a single `_elapsedTimer`
  (`MainWindowViewModel.cs` `StartElapsedTimer`, started from `App.axaml.cs`) ticks
  `UpdateRunningElapsedLabels`, refreshing BOTH the queue rows AND the stage cards in one pass
  with the same `now`. The offset is purely two different anchors: the queue row uses
  `_runStartedAt` (execute-start), the stage card uses its own `_runningSince` set at the
  Author-tests `stage_start` (`StageRowViewModel.MarkRunning(UtcNow)` from `ApplyStageEventToBoard`,
  `Helpers.cs`). `OnExecuteStarted` fires a few seconds before the stage_start event reaches the
  UI (worktree merge-back / RelayDriver spin-up + event hop), so the queue anchor is earlier and
  reads ~3s higher.
- The single-run path is already correct: `RunOneAsync` calls `BeginRunningTask` once before the
  full 11-stage `driver.RunTaskAsync`, so its `_runStartedAt` covers stage 1 onward. The bug is
  drain-specific.
- Note: `RestoreRunningTaskState` (`LiveState.cs`) does NOT seed `_runStartedAt`.

## What to build

Anchor the queue-row run-start at the task's true pipeline start in a drain, preserved across the
planning→execute handoff:

1. In `CreateDrainLifecycleCallbacks().OnPlanningStarted` (`LiveState.cs`), record
   `_runStartedAt[taskId] = DateTimeOffset.UtcNow` alongside the existing `MarkPlanning()`.
2. In `BeginRunningTask` (`LiveState.cs`), set `_runStartedAt[task.Id]` only when absent, so a
   task that planned this drain keeps its planning-start anchor while a task that skipped planning
   (stages 1–4 already Done → straight to execute) still gets execute-start. Single-run
   `RunOneAsync` is unaffected (no prior entry).
3. Clear the anchor on the planning-flag path (`OnPlanningCompleted` Flagged → `MarkIdle`) so a
   flagged-in-planning task leaves no stale `_runStartedAt` (execute's `ClearRunningTask` already
   removes it on completion).

Keep `_runStartedAt` as the single source for the queue-row elapsed — do NOT re-point it at any
stage start. Result: queue-row elapsed = wall-clock since the task's planning began (overall),
distinct from the stage card's per-stage elapsed. Decide and note the multi-task semantics in the
commit: the drain fires `OnPlanningStarted` for all planning tasks together and execute is serial,
so a later-executing task's overall elapsed includes its idle queue wait — consistent with the
"plus inter-stage gaps" framing of overall, but call it out.

## Tests

Add a focused VM test (xUnit `[AvaloniaFact]` + `[Collection("Headless")]`, mirroring
`RunningStageElapsedTests`) that pins overall-vs-stage and does NOT sleep:

- Drive the drain lifecycle via `CreateDrainLifecycleCallbacks()` (internal): fire
  `OnPlanningStarted("t")` then `OnExecuteStarted("t")`, with the run-start seeded ~10 min in the
  past. Add the minimal seam to backdate `_runStartedAt` (follow the existing
  `StageRowViewModel.MarkRunning(DateTimeOffset)` precedent of passing the start instant in).
- Mark the current stage (Author-tests) running ~4m46s ago (`stage.MarkRunning(now - 286s)`),
  call `viewModel.UpdateRunningElapsedLabels()`, and assert the running `TaskRowViewModel`'s
  `RunningElapsedLabel`/`MetricsLine` reflects the **overall** (~`10m 00s`), NOT the stage's
  ~`4m 46s`; assert `RunningElapsedLabel != stage.ElapsedLabel`.
- Regression assert: `BeginRunningTask` does not overwrite an existing planning-start anchor, and
  single-run `RunOneAsync`'s lone `BeginRunningTask` still captures a start.

Write the failing test first (confirm red), then implement.

## Done when

- In a drain, the running task's queue-row time is the overall wall-clock since the task's
  pipeline began (planning stages 1–4 included), ticking in sync with the single `_elapsedTimer`
  — no longer mirroring the current stage's elapsed nor sitting ~3s out of phase.
- The new test pins overall-vs-stage and failed before the fix.
- The full test suite and repo guards pass; no file exceeds 300 lines.
- Conventional Commit subject, e.g.
  `fix(ui): show overall task elapsed in the queue row, not the current stage time`.

## Coordination

Distinct from the open `llm-tasks/fix-stage-timer/` task. That one is about the STAGES **card**
timer resetting to 0 when switching between tasks (`StageRowViewModel._runningSince` / `ElapsedLabel`
lost when `Stages` is rebuilt for the newly selected task). THIS task is about the LEFT **queue
row** elapsed being anchored to execute-start instead of the overall task start. Different
properties (`TaskRowViewModel._runningElapsedLabel`/`_runStartedAt` vs
`StageRowViewModel._runningSince`), different root causes — not a shared fix. Both live near
`MainWindowViewModel.LiveState.cs` / `UpdateRunningElapsedLabels` and the stage_start handling, so
coordinate edits there to avoid collisions, but neither blocks the other.
