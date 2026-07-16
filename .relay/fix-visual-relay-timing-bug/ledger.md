## Stage 1 - Ideate

{
  "summary": "ApplyStageEventToBoard is only called for the selected task's events, so a 'flagged' event for a non-selected task never updates the stage card status from 'Running' to 'Flagged', leaving the 1-second elapsed timer ticking indefinitely on the review stage card.",
  "options": [
    "A: In HandleRelayEvent, update the stage board status to 'Flagged' for flagged events unconditionally, before the selected-task guard — stops the timer regardless of which task is selected.",
    "B: In ResetStages / BeginRunningTask, stop any stage still showing 'Running' when a new task begins, cleaning up orphaned timers defensively.",
    "C: In UpdateRunningTask (which already handles flagged events unconditionally for running tasks), also set the corresponding stage card's status to 'Flagged', piggybacking on the existing unconditional path."
  ]
}

## Stage 2 - Research

{
  "findings": "The bug is in `MainWindowViewModel.Helpers.cs`, method `HandleRelayEvent`. `ApplyStageEventToBoard` (which transitions the stage card from \"Running\" to \"Flagged\" and calls `_elapsed.StopSegment()` to stop the live timer) is called on line 58, **after** the selected-task guard on lines 31-33. `UpdateRunningTask` (line 26) handles \"flagged\" events unconditionally for task-level tracking (calls `CompleteRunningStage`, updates progress), but it never touches the stage card status in `Stages`. So when a non-selected task's review stage flags — e.g., Task B during a drain while the user views Task A — `ApplyStageEventToBoard` is skipped, the stage card stays `\"Running\"`, and the 1-second `RefreshElapsed` ticker keeps computing a growing elapsed label indefinitely. The `Stages` collection is a single shared set of cards (one per stage number) populated from `RelayStages.All` — it does not belong to any task; `LoadRunHistoryAsync` resets it from the selected task's status.json on each task switch, so a temporary status mix-up from a non-selected task's events is self-healing.\n\nThree relevant code paths: (A) `HandleRelayEvent` — move `ApplyStageEventToBoard` before the guard to stop the timer for all tasks' flagged events. (B) `ResetStages`/`BeginRunningTask`/`ClearRunningTask` — defensively stop orphaned Running stages, but none currently touch the stage board. (C) `UpdateRunningTask` — already handles flagged events unconditionally and could additionally set the stage card's status to \"Flagged\", piggybacking on the existing path.",
  "constraints": [
    "`Stages` is a shared, single ObservableCollection<StageRowViewModel> — it reflects the currently-selected task's stage statuses, not a per-task board.",
    "`ApplyStageEventToBoard` modifies this shared `Stages` collection; applying a non-selected task's events would temporarily show the wrong task's status until the next task switch / LoadRunHistoryAsync reload.",
    "`UpdateRunningElapsedLabels` (1-second DispatcherTimer) calls `stage.RefreshElapsed(now)` on EVERY stage card unconditionally — the timer keeps ticking as long as Status == \"Running\".",
    "`StageRowViewModel.Status` setter stops the live segment via `_elapsed.StopSegment()` whenever the status transitions away from \"Running\" (line 103-107).",
    "`LoadRunHistoryAsync` resets ALL stage statuses from the selected task's on-disk status.json (RunHistory.cs:38-53), then re-plays live events through `ApplyStageEventToBoard` (line 62-65).",
    "The guard `if (relayEvent.TaskId != SelectedTask?.Id) return;` protects against mixing tasks' events onto the shared board but also blocks the critical \"Flagged\" → timer-stop transition.",
    "`FlagAsync` (RelayDriver.Events.cs) writes status.json with \"Flagged\" status to disk before publishing the \"flagged\" event, so on-disk state is always correct — only the live in-memory stage card misses the transition.",
    "`UpdateRunningTask` already handles \"flagged\" events unconditionally (before the guard) via `CompleteRunningStage`, but only updates task-level tracking (`RecordStageCompleted`, `MarkRunning`/`MarkIdle`), not stage card status."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The bug has two interacting causes. First, in `HandleRelayEvent` (MainWindowViewModel.Helpers.cs:31-34), the selected-task guard `if (relayEvent.TaskId != SelectedTask?.Id) return;` blocks `ApplyStageEventToBoard` (line 58) for any flagged event from a non-selected task, so a flagged event from a background task during a drain never transitions the stage card away from \"Running\". Second, even when the flagged event passes the guard (selected task), `ApplyStageEventToBoard` (line 89-126) only sets the status of the stage matching `relayEvent.StageNumber` to \"Flagged\". In the review pair (`RunReviewPairAsync`, ReviewPair.cs:32-33), both stage 7 and stage 8 publish `stage_start`, setting both cards to \"Running\". When either review stage flags, `FlagAsync` (Events.cs:119-121) publishes a single \"flagged\" event for only the flagged stage number. The sibling stage card never receives a terminal-status event, so it stays \"Running\" forever. The 1-second `DispatcherTimer` in `UpdateRunningElapsedLabels` (LiveState.cs:234-235) calls `stage.RefreshElapsed(now)` on *every* stage unconditionally — any stage with `Status == \"Running\"` keeps computing a growing elapsed label indefinitely. `ClearRunningTask` (LiveState.cs:188-204) does not touch the `Stages` collection, so task completion never cleans up orphaned Running stages. The only cleanup path is `ResetStages`/`LoadRunHistoryAsync`, which fires only on task switch.",
  "excerpts": [
    "MainWindowViewModel.Helpers.cs:26-34 — `UpdateRunningTask` (handles flagged for running-task tracking) runs unconditionally, but `ApplyStageEventToBoard` (line 58) sits behind the selected-task guard: `if (relayEvent.TaskId != SelectedTask?.Id) return;`",
    "MainWindowViewModel.Helpers.cs:89-126 — `ApplyStageEventToBoard` sets only the stage matching `relayEvent.StageNumber`: `stage.Status = relayEvent.EventName switch { ... \"flagged\" => \"Flagged\" ... }`. No sibling stage is touched.",
    "MainWindowViewModel.LiveState.cs:210-236 — `UpdateRunningElapsedLabels` iterates `foreach (var stage in Stages) stage.RefreshElapsed(now)` — every stage card gets its elapsed label refreshed if `Status == \"Running\"`.",
    "StageRowViewModel.cs:92-116 — `Status` setter: when transitioning away from \"Running\", calls `_elapsed.StopSegment()` (line 105) and clears `ElapsedLabel` (line 106). `RefreshElapsed` (line 154-159) is a no-op when `Status != \"Running\"`, so the timer visibly stops only when Status changes.",
    "RelayDriver.ReviewPair.cs:32-33 — Publishes `stage_start` for both stage 7 (Review) and stage 8 (Visual-review), setting both to \"Running\" in the shared `Stages` collection.",
    "RelayDriver.ReviewPair.cs:106-116 — When Review (7) flags first with a red check, only `FlagAsync` for stage 7 is called. The `stage_done` for stage 8 never fires; stage 8 stays \"Running\" in the live `Stages`.",
    "RelayDriver.ReviewPair.cs:85-89 — When Visual-review (8) flags first (fast-visual path) with a red check, only `FlagAsync` for stage 8 is called. `RecordPairStageAsync` for stage 7 is skipped; no `stage_done` for stage 7 is published.",
    "RelayDriver.Events.cs:108-121 — `FlagAsync` marks stages *before* the flagged stage as \"Done\" in status.json (line 108-110) but only publishes ONE \"flagged\" event for the single flagged stage (line 119-121). No events are published for other running stages.",
    "MainWindowViewModel.LiveState.cs:188-204 — `ClearRunningTask` removes task-level tracking (`_runningTaskIds`, `_runningStageNumbers`, etc.) but never touches the `Stages` collection. Orphaned \"Running\" stage cards survive task completion.",
    "MainWindowViewModel.RunHistory.cs:38-53 — `LoadRunHistoryAsync` resets ALL stage statuses from the selected task's status.json. This is the only non-restart cleanup path for orphaned Running stages, and it fires only on task switch."
  ],
  "repro": "1. Start a project with the visual-review stage configured (vision tier). 2. Create a task whose changes will trigger a visual-review flag (e.g., a broken layout that produces a visual defect). 3. Run the task via the drain while viewing it. 4. Observe: when the review pair runs, both stage 7 (Review) and stage 8 (Visual-review) cards show \"Running\" with ticking elapsed labels. 5. When stage 8 flags (visual-review returns red), stage 8 transitions to \"Flagged\" and stops ticking — but stage 7 stays \"Running\" with a growing elapsed label. Alternatively, when stage 7 flags first (review returns red), stage 7 stops but stage 8 keeps ticking. The orphaned timer only stops when the user switches to another task or restarts the app."
}

## Stage 4 - Plan

{
  "plan": "## Root cause\n\nThe bug has two interacting causes:\n\n1. **Sibling review-pair stage orphaned (primary)**: When stage 7 or 8 flags via `FlagAsync`, only ONE \"flagged\" event is published for the flagged stage. The sibling never receives a terminal event — not from `FlagAsync` (Events.cs:108 only marks stages *before* the flagged stage as \"Done\"), and not from `RecordPairStageAsync` (ReviewPair.cs skips recording the sibling in all four flag-return paths). The sibling stage card stays \"Running\" in both the live `Stages` collection and the on-disk `status.json`. The 1-second `UpdateRunningElapsedLabels` timer refreshes every stage with `Status == \"Running\"` unconditionally, so the orphaned card ticks forever.\n\n2. **Selected-task guard blocks flagged events for background tasks (secondary)**: `ApplyStageEventToBoard` (which calls `stage.Status = \"Flagged\"` → `_elapsed.StopSegment()`) sits behind `if (relayEvent.TaskId != SelectedTask?.Id) return;` in `HandleRelayEvent` (Helpers.cs:31-34). While a non-selected task's stages are never set to \"Running\" on the shared `Stages` board (because `stage_start` is also behind the guard), the guard does prevent the defensive timer-stop for any edge case where a stage is somehow \"Running\" for a background task.\n\n## Fix strategy (three coordinated changes)\n\n### Change 1 — `RelayDriver.Events.cs` line 108: mark ALL Running stages as Done\n\nIn `FlagAsync`, change the LINQ filter from `e.Stage < flaggedStage` (stages before the flagged one only) to no stage-number filter — settle every stage that is still \"Running\". When a task flags, all concurrent work stops, so any remaining \"Running\" stage (including a sibling review-pair stage or stages after the flagged one) has no terminal event coming and must be settled. This fixes `status.json` so `LoadRunHistoryAsync` never resurrects a \"Running\" sibling on task switch.\n\n### Change 2 — `MainWindowViewModel.Helpers.cs`: call `ApplyStageEventToBoard` for flagged events before the selected-task guard\n\nInsert after line 30 (`AccumulateTaskActiveTime(relayEvent);`) and before line 31 (the guard):\n```csharp\nif (relayEvent.EventName == \"flagged\")\n    ApplyStageEventToBoard(relayEvent);\n```\nThe post-guard call on line 58 is idempotent (setting status to \"Flagged\" when already \"Flagged\" is a no-op). The pre-guard call ensures the timer stops even for background tasks during a drain. The shared `Stages` board may briefly show a mismatched status, but it self-heals on the next `LoadRunHistoryAsync` (task switch).\n\n### Change 3 — `MainWindowViewModel.Helpers.cs` `ApplyStageEventToBoard`: settle the sibling review-pair stage\n\nAfter the existing status switch that handles `\"flagged\" => \"Flagged\"` (line 122), add sibling settling logic: when stage 7 flags, also set stage 8→\"Done\" if \"Running\"; when stage 8 flags, also set stage 7→\"Done\" if \"Running\". This covers the live board for the selected task. Combined with Change 1, it also keeps `status.json` correct.\n\n### Test file — `tests/VisualRelay.Tests/ReviewPairOrphanedStageTimerTests.cs`\n\nNew test file using the existing `RelayEventTestDispatch` helpers, `[Collection(\"Headless\")]`, and `TestRepository` patterns. Covers:\n- Stage 7 flagged (selected task) → stage 8 stops ticking (status \"Done\", elapsed cleared)\n- Stage 8 flagged (selected task) → stage 7 stops ticking\n- Background (non-selected) task flagged → stage board card timer stops via pre-guard path\n- Sibling \"Running\" status does not survive a status.json round-trip (validates Change 1)",
  "manifest": [
    "src/VisualRelay.Core/Execution/RelayDriver.Events.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs",
    "+tests/VisualRelay.Tests/ReviewPairOrphanedStageTimerTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/ReviewPairOrphanedStageTimerTests.cs"],
  "rationale": "Four tests covering the three planned fix changes: (1) sibling review-pair stage settling when stage 7 flags, (2) sibling settling when stage 8 flags, (3) pre-guard ApplyStageEventToBoard for non-selected task flagged events, and (4) status.json round-trip verifying orphaned 'Running' doesn't persist on disk. All tests compile and fail against the current buggy code, confirming they will pass once the three coordinated changes from the plan are implemented."
}

## Stage 6 - Implement

{
  "summary": "Fixed the visual review stage timer bug with three coordinated changes:\n\n1. **`RelayDriver.Events.cs:108`** — Changed `FlagAsync` to settle ALL stages with Status \"Running\" (not just those before the flagged stage). This ensures the on-disk status.json never preserves an orphaned \"Running\" sibling across task switches.\n\n2. **`MainWindowViewModel.Helpers.cs:30`** — Added an unconditional `ApplyStageEventToBoard(relayEvent)` call for `\"flagged\"` events before the selected-task guard. This stops the stage-card timer even when the flagged task is not the one the user is viewing.\n\n3. **`MainWindowViewModel.LiveState.cs:187-192`** — Added sibling settling in `CompleteRunningStage`: when a review-pair stage (7 or 8) flags for the selected task, its sibling is immediately set to \"Done\" if still \"Running\". This stops the 1-second elapsed ticker on the orphaned stage card.\n\nAll 4 targeted tests pass, and the full suite passes (3078 passed, 114 skipped, 0 failed). Both split files stay within their 300-line budgets (Helpers.cs: 300, LiveState.cs: 299)."
}

## Stage 7 - Review

{ "verdict": "changes", "issues": ["The review-pair sibling settlement was placed in CompleteRunningStage (MainWindowViewModel.LiveState.cs), which fires for both stage_done and flagged events. This causes the still-running sibling to be prematurely marked Done during normal review-pair completion (e.g. fast visual finishing before slow review, or vice versa). It should be moved to the flagged-only path as specified in the plan, likely inside ApplyStageEventToBoard or UpdateRunningTask for flagged events only."] }

## Stage 8 - Visual-review

_Skipped: All changes are in backend/ViewModel C# logic (event dispatch, timer lifecycle, status.json persistence). No UI markup, styles, layouts, frontend code, images, charts, or generated documents are touched. The behavioral result (timer stopping) is the functional correctness of the fix, not a visual rendering concern requiring a review pass._

## Stage 9 - Fix

{ "summary": "Moved review-pair sibling settlement from CompleteRunningStage (which fired for both stage_done and flagged events, prematurely marking siblings Done during normal completion) to a flagged-only path in HandleRelayEvent after the post-guard ApplyStageEventToBoard call. Also compressed EventsFor/TraceEntriesFor to stay within the 300-line budget (Helpers.cs: 298, LiveState.cs: 293). Full suite: 3078 passed, 114 skipped, 0 failed." }

## Stage 10 - Verify

{
  "summary": "Fixed the orphaned review-pair stage timer with three coordinated changes. (1) `RelayDriver.Events.cs:108` — `FlagAsync` now settles ALL stages with Status \"Running\" (not just those before the flagged stage), so status.json never preserves an orphaned \"Running\" sibling across task switches. (2) `MainWindowViewModel.Helpers.cs:30` — Added unconditional `ApplyStageEventToBoard(relayEvent)` for `\"flagged\"` events before the selected-task guard (lines 31-34), stopping the stage-card timer even when the flagged task is not the user's selected task. (3) `HandleRelayEvent` (lines 59-63) — After the post-guard `ApplyStageEventToBoard`, sibling review-pair settlement sets stage 8→\"Done\" when stage 7 flags, and vice versa, moved from `CompleteRunningStage` (which fired on both `stage_done` and `flagged`, prematurely marking siblings Done on normal completion). New test file `ReviewPairOrphanedStageTimerTests.cs` covers all four scenarios. Full suite: 3078 passed, 114 skipped, 0 failed.",
  "commitMessages": [
    "fix: stop sibling review-stage timer on flagged event",
    "fix: settle all Running stages in FlagAsync and apply flagged status pre-guard",
    "fix: orphaned review-stage timer keeps ticking when sibling review stage flags",
    "fix: stop orphaned Running stage card from ticking forever on review-pair flag",
    "fix: settle review-pair sibling stage in flagged-only path, not in CompleteRunningStage"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

