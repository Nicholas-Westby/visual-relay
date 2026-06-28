## Stage 1 - Ideate

{
  "summary": "When switching tasks, `ApplyStageEventToBoard` replays `stage_start` events and sets `_runningSince` to `DateTimeOffset.UtcNow` instead of the actual event timestamp. This makes the elapsed timer compute `now - now ≈ 0s`, resetting the stage timer to 0. The fix is to use `relayEvent.Timestamp` (which already exists on the event) so the elapsed is computed from the real start time.",
  "options": [
    "Use `relayEvent.Timestamp` instead of `DateTimeOffset.UtcNow` in `ApplyStageEventToBoard` when calling `stage.MarkRunning(...)` (single-line change in MainWindowViewModel.Helpers.cs).",
    "Skip `stage_start` replay during task switch in `LoadRunHistoryAsync` and derive running state + start time from the status record instead of replayed events.",
    "Persist the stage start timestamp in the driver-written status record (`StageStatusEntry`) and restore it from there, surviving both task switches and app restarts."
  ]
}

## Stage 2 - Research

{
  "findings": "The bug is in `ApplyStageEventToBoard` at `MainWindowViewModel.Helpers.cs:92`, which calls `stage.MarkRunning(DateTimeOffset.UtcNow)` when replaying `stage_start` events. The elapsed timer (`StageRowViewModel.RefreshElapsed`) computes `now - _runningSince`, so using `UtcNow` makes it compute `now - now ≈ 0s`, resetting the timer to zero on every task switch.\n\nThe stage board (`Stages` collection) is **global/shared** across all tasks — one set of `StageRowViewModel` instances is reused for every task selection. When switching tasks, `SelectTaskAsync` (Commands.cs:225) → `ResetStages()` → `LoadRunHistoryAsync(task.Id)`, which replays stored live events via `ApplyStageEventToBoard` (RunHistory.cs:56-59).\n\nThe `RelayEvent.Timestamp` field (RelayEvent.cs:3, type `DateTimeOffset`) already carries the actual event timestamp from when the driver emitted it — this is accurate for both live events (during execution, where it ≈ UtcNow) and replayed events (where it preserves the original start time).\n\nThe fix is a single-line change in `MainWindowViewModel.Helpers.cs:92`: replace `DateTimeOffset.UtcNow` with `relayEvent.Timestamp`. This is the same approach validated by the existing tests in `RunningStageElapsedTests.cs` (which seed `MarkRunning` with a past timestamp and assert correct elapsed labels) and by the `StageRowViewModel.MarkRunning(DateTimeOffset)` signature (line 136) which already accepts an arbitrary start time.\n\nThe 1-second `DispatcherTimer` (`StartElapsedTimer` at MainWindowViewModel.cs:282-291) drives `UpdateRunningElapsedLabels()` (LiveState.cs:188-223), which calls `stage.RefreshElapsed(now)` for every stage card — this path needs no changes. The `_runningSince` field is cleared when status leaves \"Running\" (StageRowViewModel.cs:98-101), so a completed stage won't keep ticking.",
  "constraints": [
    "The `Stages` collection is global/shared across all tasks — do not make it per-task.",
    "`RelayEvent.Timestamp` is `DateTimeOffset` and already exists on every event — no schema changes needed.",
    "`StageRowViewModel.MarkRunning(DateTimeOffset startedAt)` already exists and accepts an arbitrary start time — types match.",
    "For live events during an active run, `relayEvent.Timestamp ≈ UtcNow` (events are processed ~instantly), so the fix preserves correct behavior during execution.",
    "For replayed events (task switch), `relayEvent.Timestamp` is the original event time from disk, preserving the correct elapsed.",
    "The 1-second timer tick (`UpdateRunningElapsedLabels` → `stage.RefreshElapsed(now)`) computes elapsed as `now - _runningSince` — with the fix, this correctly computes elapsed from the real start time.",
    "Only `stage_start` events set running state — other events (`stage_done`, `flagged`, etc.) go through the `else` branch at Helpers.cs:94-102 and don't call `MarkRunning`.",
    "`ResetStages(string? taskId)` (Stages.cs:11-39) reads the status record from disk and sets `DurationLabel` via `FormatDurationLabel` but does NOT call `MarkRunning` — it sets `stage.Status = entry.Status` directly, so it won't interfere with the timer fix.",
    "Test file `RunningStageElapsedTests.cs` validates tick behavior by calling `MarkRunning` with a past timestamp followed by `RefreshElapsed` — the fix makes the production code match what the tests already expect.",
    "All C# source files must stay under 300 lines per the project convention.",
    "The `./visual-relay check` command (format → build → tests → screenshots) must pass."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The bug is confirmed at MainWindowViewModel.Helpers.cs:92 where `stage.MarkRunning(DateTimeOffset.UtcNow)` replays `stage_start` events with the current time instead of the original event timestamp. The call chain is: `SelectTaskAsync` (Commands.cs:225) → `ResetStages()` which clears all stages → `LoadRunHistoryAsync` (RunHistory.cs:56-58) which replays stored events → `ApplyStageEventToBoard` (Helpers.cs:88-92) which calls `MarkRunning(UtcNow)`. `StageRowViewModel.MarkRunning` (StageRowViewModel.cs:136-141) stores the passed timestamp as `_runningSince`. The 1-second timer (LiveState.cs:188-223) calls `stage.RefreshElapsed(now)` which computes `now - _runningSince` (StageRowViewModel.cs:148-154). With `UtcNow` as the start, this yields ~0s on every replay. The `RelayEvent.Timestamp` field (RelayEvent.cs:4, `DateTimeOffset`) preserves the original event time from disk, making it the correct replacement. Existing tests in RunningStageElapsedTests.cs (lines 25, 51, 82) already validate this pattern by seeding `MarkRunning` with past timestamps and asserting correct elapsed labels. The `ResetStages` method (Stages.cs:11-39) does not interfere — it sets status directly without calling `MarkRunning`.",
  "excerpts": [
    "Helpers.cs:92: stage.MarkRunning(DateTimeOffset.UtcNow);",
    "RunHistory.cs:56-58: foreach (var relayEvent in liveEvents.OrderBy(item => item.Timestamp)) { ApplyStageEventToBoard(relayEvent); }",
    "Commands.cs:225,240: ResetStages(); ... await LoadRunHistoryAsync(task.Id);",
    "StageRowViewModel.cs:136-141: public void MarkRunning(DateTimeOffset startedAt) { _runningSince = startedAt; ElapsedLabel = string.Empty; Status = \"Running\"; }",
    "StageRowViewModel.cs:148-154: public void RefreshElapsed(DateTimeOffset now) { if (Status == \"Running\" && _runningSince is { } since) { ElapsedLabel = ElapsedFormatter.Label(now - since); } }",
    "LiveState.cs:188-223: UpdateRunningElapsedLabels() calls stage.RefreshElapsed(now) for every stage",
    "RelayEvent.cs:3-4: public sealed record RelayEvent(DateTimeOffset Timestamp, ...)",
    "Stages.cs:28-30: ResetStages sets stage.Status = entry.Status directly, never calls MarkRunning",
    "RunningStageElapsedTests.cs:25-29: stage.MarkRunning(pastTimestamp) → RefreshElapsed(now) → Assert.Equal(\"2m 25s\", stage.ElapsedLabel)"
  ],
  "repro": "1. Start a task run so a stage transitions to Running (a stage_start event fires and is stored). 2. While the stage is running, click a different task in the task list. 3. Click back to the original task. 4. Observe: the stage's elapsed timer shows ~0s instead of the actual elapsed time since the stage started. The bug triggers because LoadRunHistoryAsync replays the stored stage_start event through ApplyStageEventToBoard, which calls MarkRunning(DateTimeOffset.UtcNow) — resetting the _runningSince to the current moment."
}

## Stage 4 - Plan

{
  "plan": "## Fix: Stage timer resets to 0 on task switch\n\n### Root cause\nWhen switching tasks, `LoadRunHistoryAsync` replays stored `stage_start` events through `ApplyStageEventToBoard` (line 92), which calls `stage.MarkRunning(DateTimeOffset.UtcNow)` — using the *current* time instead of the original event timestamp. `StageRowViewModel.RefreshElapsed` computes `now - _runningSince`, so when `_runningSince` is `UtcNow`, the elapsed is ~0s on every replay.\n\n### Fix\nReplace `DateTimeOffset.UtcNow` with `relayEvent.Timestamp` on line 92 of `MainWindowViewModel.Helpers.cs`. The `RelayEvent.Timestamp` field (type `DateTimeOffset`) already carries the original event time from when the driver emitted it. For live events during an active run, `relayEvent.Timestamp ≈ UtcNow` (events are processed near-instantly), so behavior is unchanged. For replayed events on task switch, `relayEvent.Timestamp` preserves the original start time from disk, so elapsed computes correctly.\n\n### Change\n- **`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs:92`**: Change `stage.MarkRunning(DateTimeOffset.UtcNow)` to `stage.MarkRunning(relayEvent.Timestamp)`\n\n### Validation\nExisting tests in `RunningStageElapsedTests.cs` already validate this pattern — they seed `MarkRunning` with past timestamps (`UtcNow - TimeSpan.FromSeconds(145)`, etc.) and assert correct elapsed labels. This fix aligns the production replay path with what the tests already expect.\n\nThe `./visual-relay check` command (format → build → tests → screenshots) must pass.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs"
  ]
}

> **Manifest merge (stage 5)**: added 1 authored test file(s).

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/RunningStageElapsedTests.cs"],
  "rationale": "Added ReplayedStageStart_PreservesOriginalTimestamp test that fires a stage_start RelayEvent with a 5-minute-old timestamp through HandleRelayEvent (the same entry point used by both live events and LoadRunHistoryAsync replay). The test then calls UpdateRunningElapsedLabels() and asserts the ElapsedLabel is NOT \"0s\" and contains a minute marker. With the current buggy code (MarkRunning(DateTimeOffset.UtcNow)), the elapsed is \"0s\" — test fails. After the fix (MarkRunning(relayEvent.Timestamp)), the elapsed will reflect the ~5m original event age — test passes. This directly exercises the ApplyStageEventToBoard replay path that resets the timer on task switch."
}

## Stage 6 - Implement

{
  "summary": "Fixed the stage timer reset bug by changing line 92 of MainWindowViewModel.Helpers.cs from `stage.MarkRunning(DateTimeOffset.UtcNow)` to `stage.MarkRunning(relayEvent.Timestamp)`. When replaying `stage_start` events during task switching, the code now uses the original event timestamp instead of the current time, preserving the correct elapsed time. All 2051 tests pass."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Fix already applied: MainWindowViewModel.Helpers.cs:92 uses relayEvent.Timestamp instead of DateTimeOffset.UtcNow when replaying stage_start events. All 5 RunningStageElapsed tests pass (95ms, 0 failures), including the ReplayedStageStart_PreservesOriginalTimestamp regression test. No other MarkRunning(DateTimeOffset.UtcNow) calls exist in the codebase." }

## Stage 9 - Verify

{
  "summary": "Fixed stage timer reset on task switch by using `relayEvent.Timestamp` instead of `DateTimeOffset.UtcNow` in `ApplyStageEventToBoard` when replaying `stage_start` events",
  "commitMessages": [
    "fix: preserve stage elapsed timer across task switches by using event timestamp",
    "fix: use relayEvent.Timestamp when replaying stage_start to avoid resetting elapsed",
    "fix: keep running stage timer accurate when switching tasks",
    "fix: stop stage timer resetting to 0 on task switch"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

