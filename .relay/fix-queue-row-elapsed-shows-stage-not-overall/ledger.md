## Stage 1 - Ideate

{
  "summary": "Fix the queue-row elapsed timer in a \"Run All\" drain so it shows overall wall-clock since the task's pipeline began (planning stages 1–4 inclusive), rather than only the current execute-stage time. Root cause: `_runStartedAt` is set only at `OnExecuteStarted` (stage 5), missing the planning phase. The fix adds a planning-start anchor in `OnPlanningStarted`, guards `BeginRunningTask` from overwriting it, and clears it on the planning-flag path. A new VM test pins the overall-vs-stage distinction.",
  "options": [
    "Option A — Anchor-at-planning, guard-on-execute: Record `_runStartedAt` in `OnPlanningStarted`; make `BeginRunningTask` use `TryAdd` so it preserves an existing planning anchor; clear the anchor in `OnPlanningCompleted` (Flagged path). Minimal diff, single-source contract preserved.",
    "Option B — Relocate the anchor earlier in the drain: Change `RelayQueueController.DrainAsync` to fire `OnExecuteStarted` before the parallel planning phase. No new writer needed, but breaks callback semantics and has higher blast radius.",
    "Option C — Lazy-compute earliest anchor on read: Add a separate `_planningStartedAt` dictionary written only by `OnPlanningStarted`; `UpdateRunningElapsedLabels` takes `min()` of both anchors. No guard needed in `BeginRunningTask`, but introduces a second dictionary and more complex read logic."
  ]
}

## Stage 2 - Research

{
  "findings": "The fix targets `MainWindowViewModel.LiveState.cs` (279 lines). The root cause: `_runStartedAt[taskId]` is only written in `BeginRunningTask` (line 112), which sets it to `DateTimeOffset.UtcNow` unconditionally via direct indexer assignment. In a drain, `BeginRunningTask` is called from `OnExecuteStarted` (line 40 of LiveState.cs), which fires at the start of Phase 2 (serial execute, stage 5). Phase 1 (parallel planning of stages 1–4) only calls `OnPlanningStarted`, which currently does NOT record any start time — it only calls `StatusText = …` and `task.MarkPlanning()`. So the queue-row elapsed (`_runningElapsedLabel`, written in `UpdateRunningElapsedLabels` at line 195 via `ElapsedFormatter.Label(now - _runStartedAt[taskId])`) reflects only the execute-phase duration. The stage-card elapsed uses a completely separate anchor — `StageRowViewModel._runningSince` set by `stage.MarkRunning(DateTimeOffset.UtcNow)` in `ApplyStageEventToBoard` (`Helpers.cs` line 92) — which explains the ~3s phase offset (the drain's `OnExecuteStarted` fires a few seconds before the `stage_start` RelayEvent reaches the UI).\n\nThree changes are needed:\n1. **OnPlanningStarted** (line 16-18 of LiveState.cs): add `_runStartedAt[taskId] = DateTimeOffset.UtcNow` alongside the existing `MarkPlanning()`.\n2. **BeginRunningTask** (line 106-119): change `_runStartedAt[task.Id] = DateTimeOffset.UtcNow` to `_runStartedAt.TryAdd(task.Id, DateTimeOffset.UtcNow)` so an existing planning-start anchor is preserved.\n3. **OnPlanningCompleted** Flagged→MarkIdle path (line 24-26): add `_runStartedAt.Remove(taskId)` so a flagged-in-planning task leaves no stale anchor.\n\nThe single-run path (`RunOneAsync` in `Execution.cs` line 267) is unaffected because it calls `BeginRunningTask` once before `driver.RunTaskAsync` with no prior `_runStartedAt` entry. `ClearRunningTask` (line 161-179) already removes the `_runStartedAt` entry on completion.\n\nThe test needs a seam to backdate `_runStartedAt`, mirroring `StageRowViewModel.MarkRunning(DateTimeOffset)` which accepts the start instant as a parameter. A minimal internal method like `internal void SetRunStartedAt(string taskId, DateTimeOffset startedAt) { _runStartedAt[taskId] = startedAt; }` on `MainWindowViewModel` follows that precedent and lets the test seed an anchor ~10 min in the past before driving `UpdateRunningElapsedLabels()`.\n\nExisting test pattern: `TaskDetailErrorRefreshTests` (136 lines, `[Collection(\"Headless\")]`) drives `CreateDrainLifecycleCallbacks()` directly and calls `lifecycle.OnExecuteStarted.Invoke(…)`, `Dispatcher.UIThread.RunJobs()`, etc. The new test file should mirror `RunningStageElapsedTests` (91 lines) and sit alongside it under `tests/VisualRelay.Tests/`.",
  "constraints": [
    "MainWindowViewModel.cs is exactly 300 lines (at the limit) — no changes can be added there.",
    "MainWindowViewModel.LiveState.cs is 279 lines — can accommodate roughly ~21 more lines, enough for the three small changes plus the internal test seam.",
    "MainWindowViewModel.Execution.cs is 299 lines — must not add anything here.",
    "MainWindowViewModel.Helpers.cs is 299 lines — must not add anything here.",
    "All source .cs files must stay ≤ 300 lines (project convention enforced by command-guard).",
    "The test class must carry [Collection(\"Headless\")] because it uses [AvaloniaFact] (Avalonia headless UI tests share a single process-global dispatcher).",
    "The test must not sleep — it seeds past timestamps and calls UpdateRunningElapsedLabels() directly.",
    "The fix must keep _runStartedAt as the single source for queue-row elapsed — must not re-point it at any stage start.",
    "The test must assert both RunningElapsedLabel (overall ~10m) and assert it differs from the stage's ElapsedLabel (~4m46s).",
    "Regression assert: BeginRunningTask does not overwrite an existing planning-start anchor, and single-run RunOneAsync's lone BeginRunningTask still captures a start.",
    "DrainLifecycleCallbacks is a simple record in Core (28 lines) — must not change its signature to preserve the single responsibility of the callback pattern.",
    "The test must fail before the fix (red phase) — the current BeginRunningTask unconditionally overwrites _runStartedAt, so an OnPlanningStarted-only anchor (if added) would be clobbered; after the TryAdd guard it should be preserved.",
    "The drain fires OnPlanningStarted for all planning tasks together and execute is serial; a later-executing task's overall elapsed includes its idle queue wait — this must be documented in the commit message."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The queue-row elapsed shows only the execute-phase time because `_runStartedAt` is set solely by `BeginRunningTask` (LiveState.cs:112), which in a drain is called from `OnExecuteStarted` (LiveState.cs:40) — the start of Phase 2 / stage 5. Phase 1's `OnPlanningStarted` (LiveState.cs:15-18) only calls `MarkPlanning()` with no `_runStartedAt` record. The stage card has a separate `_runningSince` anchor (StageRowViewModel.cs:138), explaining the ~3s offset. The single `_elapsedTimer` / `UpdateRunningElapsedLabels()` (LiveState.cs:188-201) refreshes both labels in one pass with the same `now`, so the offset is purely due to the two different anchors. The single-run path (Execution.cs:267) is correct: `BeginRunningTask` fires before `driver.RunTaskAsync`, covering stage 1 onward. The drain's `RelayQueueController.DrainAsync` (line 131-134, 207) fires `OnPlanningStarted` for Phase 1 and `OnExecuteStarted` for Phase 2 — but only the latter records `_runStartedAt`.",
  "excerpts": [
    "MainWindowViewModel.cs:38 — `private readonly Dictionary<string, DateTimeOffset> _runStartedAt = new(StringComparer.Ordinal);` — single anchor for queue-row elapsed",
    "LiveState.cs:112 — `_runStartedAt[task.Id] = DateTimeOffset.UtcNow;` — ONLY writer, unconditional indexer assignment in BeginRunningTask",
    "LiveState.cs:40 — `BeginRunningTask(task);` — called from OnExecuteStarted (drain execute-phase callback, stage 5)",
    "LiveState.cs:15-18 — OnPlanningStarted: only calls MarkPlanning(), no _runStartedAt recording",
    "LiveState.cs:195 — `ElapsedFormatter.Label(now - startedAt)` — queue-row elapsed computed from _runStartedAt only",
    "Execution.cs:267 — `BeginRunningTask(task);` — single-run path: called before driver.RunTaskAsync, covers stage 1 onward (correct)",
    "Helpers.cs:92 — `stage.MarkRunning(DateTimeOffset.UtcNow);` — stage-card elapsed uses separate _runningSince anchor",
    "StageRowViewModel.cs:138 — `_runningSince = startedAt;` — per-stage anchor, source of the ~3s offset",
    "RelayQueueController.cs:131-134 — `_lifecycle.OnPlanningStarted?.Invoke(taskId);` — Phase 1 (parallel planning) fires this, no start time captured",
    "RelayQueueController.cs:207 — `_lifecycle?.OnExecuteStarted?.Invoke(task.Id);` — Phase 2 (serial execute) fires this, _runStartedAt recorded here",
    "LiveState.cs:188-201 — `UpdateRunningElapsedLabels()` refreshes BOTH queue rows AND stage cards in one pass with the same `now` — single timer, two different anchors",
    "MainWindowViewModel.cs:300 lines (at limit), LiveState.cs:279 (room for ~21), Execution.cs:299 (no room), Helpers.cs:299 (no room)"
  ],
  "repro": "1. Create a task whose planning stages 1–4 (Ideate/Research/Diagnose/Plan) are not already Done. 2. Initiate a 'Run All' drain. 3. Wait until Phase 2 begins (serial execute, stage 5 Author-tests). 4. Observe the LEFT queue list's running-task row: it shows e.g. 'Stage 05 · Author-tests · 4m 49s' — the elapsed counts from execute-start (OnExecuteStarted), not from planning-start (OnPlanningStarted). The task may have been running ~10+ minutes (planning stages included) but the queue row shows only ~5 minutes (execute phase only). 5. Compare with the STAGES card which shows 'Running 4m 46s' — a ~3s offset because its `_runningSince` anchor is set by `stage.MarkRunning(DateTimeOffset.UtcNow)` in ApplyStageEventToBoard, which fires a few seconds after OnExecuteStarted due to worktree merge-back and RelayDriver event hop."
}

## Stage 4 - Plan

{
  "plan": "## Root cause\nIn a \"Run All\" drain, `_runStartedAt[taskId]` is set only in `BeginRunningTask` (LiveState.cs:112), which the drain calls from `OnExecuteStarted` — the Phase 2 serial-execute entry point (stage 5). Phase 1 parallel planning (stages 1–4, driven by `OnPlanningStarted`) only calls `MarkPlanning()` with no start-time recording. So the queue-row elapsed computed in `UpdateRunningElapsedLabels` (line 195) measures only execute-phase time.\n\nThe stage card uses a completely separate `_runningSince` anchor (`stage.MarkRunning(UtcNow)` in Helpers.cs:92), explaining the ~3s offset. A single `_elapsedTimer` refreshes both labels in one pass with the same `now` — the offset is purely the two different anchors.\n\nThe single-run path (`RunOneAsync` in Execution.cs:267) is already correct: `BeginRunningTask` fires once before `driver.RunTaskAsync`, covering stage 1 onward.\n\n## Changes — all in MainWindowViewModel.LiveState.cs (279→286 lines, within 300-line ceiling)\n\n### 1. OnPlanningStarted (line 17): record planning-start anchor\nAfter `StatusText = $\"Planning {taskId}…\";`, insert:\n```csharp\n_runStartedAt[taskId] = DateTimeOffset.UtcNow;\n```\nSo Phase 1 captures the overall task start, and the queue-row elapsed immediately reflects wall-clock since planning began.\n\n### 2. OnPlanningCompleted Flagged path (lines 25–26): clear stale anchor\nWrap in braces and add `_runStartedAt.Remove(taskId)` so a flagged-in-planning task leaves no stale entry (execute's `ClearRunningTask` already removes it on the execute path, but a plan-phase flag never reaches execute):\n```csharp\nif (status == RelayTaskOutcomeStatus.Flagged)\n{\n    _runStartedAt.Remove(taskId);\n    task.MarkIdle();\n}\n```\n\n### 3. BeginRunningTask (line 112): guard with TryAdd\nChange unconditional indexer assignment to `TryAdd` so a task that planned this drain keeps its planning-start anchor:\n```csharp\n_runStartedAt.TryAdd(task.Id, DateTimeOffset.UtcNow);\n```\nSingle-run `RunOneAsync` is unaffected: no prior entry exists, so `TryAdd` succeeds. A task skipping planning (stages 1–4 already Done → straight to execute) also gets its execute-start via the successful `TryAdd`.\n\n### 4. Internal test seam (after line 278, before closing `}`)\n```csharp\ninternal void SetRunStartedAt(string taskId, DateTimeOffset startedAt)\n{\n    _runStartedAt[taskId] = startedAt;\n}\n```\nMirrors the `StageRowViewModel.MarkRunning(DateTimeOffset)` precedent of accepting a start instant as a parameter.\n\n## New test file: tests/VisualRelay.Tests/QueueRowElapsedOverallTests.cs (~130 lines)\n\nPattern: `[Collection(\"Headless\")]` + `[AvaloniaFact]`, mirroring RunningStageElapsedTests / TaskDetailErrorRefreshTests. No sleeping — seeds past timestamps and calls `UpdateRunningElapsedLabels()` directly.\n\n### Test 1: Drain_RunningTaskElapsed_ShowsOverallTime_NotStageTime\n- Create TestRepository with config + task \"gamma\".\n- New MainWindowViewModel, LoadInitialAsync().\n- `viewModel.RestoreRunningTaskState(\"gamma\", stageNumber: 5, stageName: \"Author-tests\")` → populates `_runningTaskIds`.\n- `viewModel.SetRunStartedAt(\"gamma\", UtcNow - 600s)` → seeds 10-min-ago anchor.\n- Get Author-tests stage row (`Stages.First(s => s.Number == 5)`), call `stage.MarkRunning(UtcNow - 286s)` → stage running 4m46s ago.\n- `viewModel.UpdateRunningElapsedLabels()`.\n- `var row = viewModel.Tasks.First(t => t.Id == \"gamma\")`.\n- Assert `row.RunningElapsedLabel == \"10m 00s\"`.\n- Assert `row.MetricsLine.Contains(\"10m 00s\")`.\n- Assert `stage.ElapsedLabel == \"4m 46s\"`.\n- Assert `row.RunningElapsedLabel != stage.ElapsedLabel`.\n\n### Test 2: BeginRunningTask_PreservesExistingPlanningAnchor\n- `RestoreRunningTaskState(\"gamma\", 5, \"Author-tests\")`.\n- `SetRunStartedAt(\"gamma\", UtcNow - 600s)`.\n- Create lifecycle, `lifecycle.OnExecuteStarted.Invoke(\"gamma\")` → `BeginRunningTask` fires, `TryAdd` is a no-op.\n- `UpdateRunningElapsedLabels()`.\n- Assert `row.RunningElapsedLabel == \"10m 00s\"` (NOT ~\"0s\"), proving the seeded planning-start anchor survived the execute handoff.\n\n### Test 3: BeginRunningTask_CapturesStart_WhenNoPriorAnchor\n- `RestoreRunningTaskState(\"gamma\", 5, \"Author-tests\")`.\n- Do NOT seed `_runStartedAt`.\n- `lifecycle.OnExecuteStarted.Invoke(\"gamma\")` → `TryAdd` succeeds (no prior entry).\n- `UpdateRunningElapsedLabels()`.\n- Assert `row.RunningElapsedLabel` is not null/empty (it captured a start on first `BeginRunningTask`).\n\n## Multi-task semantic note (commit message)\nThe drain fires `OnPlanningStarted` for all planning tasks together; execute is serial. A later-executing task's overall elapsed includes its idle queue wait between plan completion and execute start — consistent with the \"overall wall-clock since pipeline began\" framing. Call this out in the commit body.\n\n## Conventional Commit subject\n`fix(ui): show overall task elapsed in the queue row, not the current stage time`",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs",
    "+tests/VisualRelay.Tests/QueueRowElapsedOverallTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 1.

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/QueueRowElapsedOverallTests.cs"],
  "rationale": "Root cause: _runStartedAt was set only in BeginRunningTask (called from OnExecuteStarted at Phase 2 / stage 5). In a drain, Phase 1 parallel planning (stages 1–4) fires OnPlanningStarted which only called MarkPlanning() — no start-time recording. Three changes fix it: (1) OnPlanningStarted records _runStartedAt[taskId] = UtcNow to anchor at pipeline start; (2) BeginRunningTask uses TryAdd so an existing planning anchor survives the execute handoff; (3) OnPlanningCompleted Flagged path removes the stale anchor. The single _elapsedTimer refreshes both queue rows and stage cards in one pass — the ~3s offset was purely from different anchors (queue: execute-start vs stage: stage_start event), now eliminated because both tick from the same timer. Single-run path (RunOneAsync) is unaffected: BeginRunningTask fires once with no prior _runStartedAt entry, TryAdd succeeds, covering stage 1 onward as before."
}

## Stage 6 - Implement

{
  "summary": "Made 4 edits to `src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs` (279→295 lines):\n\n1. **OnPlanningStarted** (line 18): Added `_runStartedAt[taskId] = DateTimeOffset.UtcNow;` to capture the overall task start when the drain's Phase 1 parallel planning begins.\n\n2. **OnPlanningCompleted Flagged path** (lines 27-30): Wrapped in braces and added `_runStartedAt.Remove(taskId);` so a flagged-in-planning task leaves no stale anchor.\n\n3. **BeginRunningTask** (line 116): Changed `_runStartedAt[task.Id] = DateTimeOffset.UtcNow` → `_runStartedAt.TryAdd(task.Id, DateTimeOffset.UtcNow)` so an existing planning-start anchor survives the execute handoff.\n\n4. **SetRunStartedAt** test seam (lines 291-294): Internal method so tests can backdate the anchor without real wall-clock wait.\n\nThe test file `tests/VisualRelay.Tests/QueueRowElapsedOverallTests.cs` (115 lines, already created in stage 5) verifies the fix with 3 focused tests — all pass."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "All 3 tests pass (3/3, 96 ms). The implementation was already applied in stages 5-6: OnPlanningStarted records _runStartedAt[taskId] at planning start (line 18), BeginRunningTask uses TryAdd to preserve it (line 116), and OnPlanningCompleted Flagged path cleans up stale entries (line 28). The test seam SetRunStartedAt (line 291) enables backdating in tests. Both files are under 300 lines (LiveState.cs: 295, test: 115)." }

## Stage 9 - Verify

{
  "summary": "Fix queue-row elapsed in a \"Run All\" drain so it shows overall wall-clock since the task's pipeline began (planning stages 1–4 included), rather than only the current execute-stage time. Root cause: `_runStartedAt` was set only at `OnExecuteStarted` (stage 5), missing the planning phase. Three changes in `LiveState.cs` (295 lines): record anchor in `OnPlanningStarted`, guard `BeginRunningTask` with `TryAdd`, clean up on `OnPlanningCompleted` Flagged path. New test file (115 lines) with 3 `[AvaloniaFact]` tests pins overall-vs-stage distinction, anchor preservation, and no-prior-anchor fallback — all 3 pass.",
  "commitMessages": [
    "fix(ui): show overall task elapsed in the queue row, not the current stage time",
    "fix: anchor drain queue-row timer at planning start, not execute start",
    "fix(ui): preserve overall task elapsed across drain planning-to-execute handoff",
    "fix: record _runStartedAt in OnPlanningStarted so queue row reflects pipeline wall-clock",
    "fix(ui): drain queue row now ticks overall elapsed including planning stages"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

