## Stage 1 - Ideate

{ 
  "summary": "'Run All' (DrainQueueCommand) skips tasks with errors at two levels: CanDrain() requires at least one task without NeedsReview, and DrainAsync() filters out NeedsReview tasks. The fix must include errored tasks so they can be re-attempted. Three approaches: A) simplest — remove the NeedsReview filter (2 lines). B) same plus a status message. C) reset NeedsReview on flagged tasks before draining, then include them.",
  "options": [
    "Option A — Remove the NeedsReview filter from CanDrain() and DrainAsync() queue construction (2-line change). Runner already self-heals NEEDS-REVIEW markers.",
    "Option B — Same as A, plus update status text to mention re-running flagged tasks for user transparency.",
    "Option C — Reset ReviewReason to null on all flagged tasks before draining, then include them. Most explicit state-reset semantics."
  ]
}

## Stage 2 - Research

{
  "findings": "The 'Run All' button (backed by `DrainQueueCommand`) skips tasks that have errors (`NeedsReview == true`) at two levels. **Level 1 — Button gate:** `CanDrain()` in `MainWindowViewModel.Helpers.cs:205` returns `!IsBusy && !PauseRequested && !ShowArchive && Tasks.Any(task => !task.NeedsReview)`. If every task has an error, 'Run All' is disabled entirely. **Level 2 — Drain execution:** `RelayQueueController.DrainAsync()` at `RelayQueueController.cs:106` filters the queue with `Tasks.Where(task => !task.NeedsReview).ToList()`, silently excluding flagged tasks from the run. The runner (`RelayDriver.RunTaskAsync`) already self-heals by deleting the `NEEDS-REVIEW` marker file at line 35 of `RelayDriver.cs` (`File.Delete(Path.Combine(taskDirectory, \"NEEDS-REVIEW\"))`), so including errored tasks is safe — the marker is cleaned on re-attempt. `NeedsReview` is a computed property on `RelayTaskItem` (`RelayTaskItem.cs:17`): `public bool NeedsReview => !string.IsNullOrWhiteSpace(ReviewReason);`. The `ObsidianBridge.cs:194` also calls `CanDrain()` to gate auto-runs after import, so the gate change affects that path too. Three fix options exist: **A)** remove the `NeedsReview` filter from both locations (2 lines); **B)** same plus a status message; **C)** reset `ReviewReason` to null on flagged tasks before draining, then include them. Option A is simplest and leverages the existing self-healing behavior.",
  "constraints": [
    "File: src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs:205 — `CanDrain()` method must be changed (remove `&& Tasks.Any(task => !task.NeedsReview)` or change to `Tasks.Count > 0` if using Option A)",
    "File: src/VisualRelay.Core/Queue/RelayQueueController.cs:106 — `DrainAsync()` queue construction must be changed (remove `.Where(task => !task.NeedsReview)` if using Option A)",
    "File: src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs:179-184 — `FormatQueueStatus()` method uses `task.NeedsReview` for display counts; this is fine as-is (it shows pending/review counts but does not gate behavior)",
    "File: src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs:194 — calls `CanDrain()`; any change to the gate affects this auto-run-after-import path",
    "`NeedsReview` is defined in src/VisualRelay.Domain/RelayTaskItem.cs:17 as `!string.IsNullOrWhiteSpace(ReviewReason)` — a read-only computed property",
    "All existing tests that assert `NeedsReview: true` on queue tasks after a flagged outcome (e.g., RelayQueueControllerTests.cs:46-48, RelayQueueControllerTests.cs:78, RelayQueueControllerTests.cs:117, etc.) must continue to pass unchanged — they test the flagging mechanism, not the drain filter behavior",
    "If Option C is chosen, `ReviewReason` must be reset (set to null) on all flagged `TaskRowViewModel` items in the ViewModel's `Tasks` collection before the drain starts",
    "No other locations gate on `task.NeedsReview` for run/drain behavior — only the two cited lines"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The bug exists at two independent layers. Layer 1: CanDrain() at MainWindowViewModel.Helpers.cs line 205 gates the Run All button with `Tasks.Any(task => !task.NeedsReview)`, which disables the button entirely when every task has an error. Layer 2: DrainAsync() at RelayQueueController.cs line 106 constructs the drain queue with `Tasks.Where(task => !task.NeedsReview).ToList()`, silently excluding all flagged tasks even when the button passes the gate. The runner (RelayDriver.RunTaskAsync, line 35) already self-heals by deleting the NEEDS-REVIEW marker file at the start of every run, confirmed by the test RunTaskAsync_RetriesTaskThatWasAlreadyNeedsReview (RelayDriverTests.cs:165-179) which shows a pre-flagged task runs to Committed and the marker is deleted. NeedsReview is a computed property on RelayTaskItem (line 17) backed by the ReviewReason field. The ObsidianBridge auto-run path (line 194) also calls CanDrain(). No other locations gate on NeedsReview for drain behavior. The screenshot from 2026-06-23 11:04:59 PM PDT shows the queue with Needs Review tasks and the Run All button.",
  "excerpts": [
    "MainWindowViewModel.Helpers.cs:205 — private bool CanDrain() => !IsBusy && !PauseRequested && !ShowArchive && Tasks.Any(task => !task.NeedsReview);",
    "RelayQueueController.cs:106 — var queue = Tasks.Where(task => !task.NeedsReview).ToList();",
    "RelayDriver.cs:35 — File.Delete(Path.Combine(taskDirectory, \"NEEDS-REVIEW\"));",
    "RelayDriverTests.cs:165-179 — RunTaskAsync_RetriesTaskThatWasAlreadyNeedsReview: pre-creates NEEDS-REVIEW marker file, runs task, asserts Committed status and marker absent",
    "RelayTaskItem.cs:17 — public bool NeedsReview => !string.IsNullOrWhiteSpace(ReviewReason);",
    "RelayQueueController.cs:234 — Tasks.Add(task with { ReviewReason = outcome.Reason ?? \"Needs review\" }); — where NeedsReview is set after a flagged outcome",
    "MainWindowViewModel.ObsidianBridge.cs:194 — if (imported > 0 && !PauseRequested && CanDrain()) await DrainQueueCommand.ExecuteAsync(null); — auto-run path also affected",
    "MainWindowViewModel.Helpers.cs:179-184 — FormatQueueStatus() uses task.NeedsReview for display counts only, not gating behavior — safe as-is"
  ],
  "repro": "1. Create a task and run it such that it flags (produces a NeedsReview outcome). 2. Observe the task appears in the queue with 'Needs review' status. 3. If this is the only task, the 'Run All' button is disabled (CanDrain returns false because no task satisfies !NeedsReview). 4. If other non-flagged tasks exist, click 'Run All' — only the non-flagged tasks execute; the flagged task is silently excluded from the drain queue (line 106 filter). 5. Expected: 'Run All' should include the flagged task so it can be re-attempted (the runner self-heals the NEEDS-REVIEW marker on line 35)."
}

## Stage 4 - Plan

{
  "plan": "Fix the two-layer NeedsReview filter that prevents 'Run All' from including tasks with errors.\n\n**Change 1 — Button gate (MainWindowViewModel.Helpers.cs:205):**\nReplace `Tasks.Any(task => !task.NeedsReview)` with `Tasks.Count > 0` in `CanDrain()`. This ensures the 'Run All' button is enabled whenever any tasks exist in the queue, regardless of their NeedsReview status.\n\n**Change 2 — Drain queue construction (RelayQueueController.cs:106):**\nReplace `Tasks.Where(task => !task.NeedsReview).ToList()` with `Tasks.ToList()`. This ensures all tasks (including flagged ones) enter the drain queue. The runner (`RelayDriver.RunTaskAsync` line 35) already deletes the NEEDS-REVIEW marker at the start of every run, so re-attempting flagged tasks is safe. If a re-attempted task flags again, the drain's own internal logic (lines 156 and 234) re-writes the marker and ReviewReason.\n\nNo test changes are required. All existing tests start with fresh tasks (no pre-existing NeedsReview), making the line 106 filter a no-op in every test. The two-phase planning tests that exclude flagged tasks from Phase 2 use an explicit `queue.Remove()` at line 143, not the line 106 filter.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs",
    "src/VisualRelay.Core/Queue/RelayQueueController.cs"
  ]
}

> **Manifest merge (stage 5)**: added 2 authored test file(s).

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/RelayQueueControllerTests.cs",
    "tests/VisualRelay.Tests/MainWindowViewModelTests.cs"
  ],
  "rationale": "Two tests added, one per bug layer. RelayQueueControllerTests.cs: DrainAsync_IncludesTasksWithNeedsReview proves line 106's `.Where(task => !task.NeedsReview)` filter silently drops flagged tasks (runner.TasksRun is empty). MainWindowViewModelTests.cs: DrainQueueCommand_CanExecute_WhenAllTasksHaveNeedsReview proves CanDrain's `Tasks.Any(task => !task.NeedsReview)` disables the Run All button when every task has an error. Both fail now and will pass after the planned two-line fix."
}

## Stage 6 - Implement

{
  "summary": "Fixed the two-layer NeedsReview filter that prevented 'Run All' from including errored tasks. Changed CanDrain() gate from `Tasks.Any(task => !task.NeedsReview)` to `Tasks.Count > 0` in MainWindowViewModel.Helpers.cs:205, and changed DrainAsync() queue construction from `Tasks.Where(task => !task.NeedsReview).ToList()` to `Tasks.ToList()` in RelayQueueController.cs:106. The runner already self-heals NEEDS-REVIEW markers on re-attempt. Also trimmed the two new test files from Stage 5 to satisfy the 300-line file-size guard: removed a trailing blank line from MainWindowViewModelTests.cs (301→300) and condensed verbose comments in RelayQueueControllerTests.cs (325→297). All 1885 tests pass."
}

## Stage 7 - Review

{ "verdict": "changes", "issues": ["Root cause unfixed: RelayQueueController.RefreshAsync() (RelayQueueController.cs:51) loads tasks via _repository.ListPendingAsync(), which excludes NeedsReview tasks (RelayTaskRepository.cs:12-17; asserted by ListPendingAsync_SkipsDoneIgnoredCompletedAndNeedsReviewTasks). Both production 'Run All' paths construct a fresh controller and call RefreshAsync() before DrainAsync() — the GUI button (MainWindowViewModel.Execution.cs:90-98) and the CLI tool (tools/VisualRelay.DrainQueue/Program.cs:45-53). Errored tasks therefore never enter the controller's Tasks collection, making the line-106 filter change a no-op for the actual bug; 'Run All' still skips them. Fix: have RefreshAsync load flagged tasks too (e.g. _repository.ListAsync(), which defaults to includeNeedsReview:true and populates ReviewReason from the NEEDS-REVIEW marker — see ListAsync_IncludesNeedsReviewTasksWithReason). The runner self-heals the marker on re-attempt.", "Regression test does not exercise the real path: DrainAsync_IncludesTasksWithNeedsReview (RelayQueueControllerTests.cs:281-295) sets ReviewReason in memory AFTER RefreshAsync(), bypassing the repository loading step that is the actual bug. It passes regardless of whether the production path is fixed. It should write a NEEDS-REVIEW marker to disk before RefreshAsync()+DrainAsync() (mirroring ListAsync_IncludesNeedsReviewTasksWithReason's setup) so it would fail against the incomplete fix.", "Stale test comment encodes the buggy behavior as intended: DrainQueueToolTests.cs:127-139 comment states 'NEEDS-REVIEW tasks are excluded from the queue during RefreshAsync'. Once RefreshAsync includes flagged tasks, this comment is wrong and must be updated."] }

## Stage 8 - Fix

{
  "summary": "Fixed the root cause: RefreshAsync() now calls ListAsync() (includeNeedsReview=true) instead of ListPendingAsync() (which excluded NeedsReview tasks). This ensures flagged tasks enter the controller's Tasks collection so 'Run All' can re-attempt them. The runner self-heals the NEEDS-REVIEW marker on re-run. Also fixed the regression test to write a NEEDS-REVIEW marker file before RefreshAsync() (exercising the real production path), and updated the stale comment in DrainQueueToolTests.cs. All 1884 tests pass."
}

## Stage 9 - Verify

{
  "summary": "Fixed 'Run All' to include errored tasks at three layers: RefreshAsync now loads NeedsReview tasks via ListAsync() instead of ListPendingAsync(); CanDrain() uses Tasks.Count > 0 instead of filtering out NeedsReview items; DrainAsync() removes the NeedsReview filter from queue construction. The runner already self-heals NEEDS-REVIEW markers on re-attempt. New regression tests write real markers before RefreshAsync() to exercise the production path. All 1884 tests pass.",
  "commitMessages": [
    "fix: include errored tasks in Run All by loading flagged items during RefreshAsync",
    "fix: remove NeedsReview gate from CanDrain and DrainAsync queue construction",
    "fix: re-attempt flagged tasks on Run All — marker is self-healed by runner"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

