## Stage 1 - Ideate

{
  "summary": "When a process/task is running, the attachment button (e.g., for adding screenshots) is disabled globally for all tasks, including those that are completely unrelated and not actively running. The fix should scope the disabled state to only the task that is actively running, allowing edits (including attachment additions) on all other tasks.",
  "options": [
    "Scope the attachment-button disabled state to the actively running task's ID. Only disable the button when the task being edited matches the currently running task; all other tasks keep the button enabled.",
    "Replace the global 'is any task running?' boolean flag with a 'running task ID' reference. Pass this ID alongside each task's own ID to determine whether that specific task can accept edits/attachments.",
    "Introduce a per-task lock on running tasks. When a task starts running, only that task enters an 'edits disabled' state; other tasks remain fully editable. Reset the lock when the task finishes or is cancelled."
  ]
}

## Stage 2 - Research

{
  "findings": "The bug is in `MainWindowViewModel.Authoring.cs` line 182–183. `CanAddAttachments()` uses the global `!IsBusy` flag, which is `true` whenever ANY task is running (set by `RunBusyAsync` wrapping `RunOneAsync` in `Execution.cs` lines 28–32). This disables the Add Attachment button for ALL tasks, even unrelated non-running ones. The fix should follow the same pattern as `CanEditSelectedTask()` (line 36) and `CanRewriteSelected()` (Rewrite.cs line 183), which correctly check `_runningTaskIds.Contains(SelectedTask.Id)` instead of the global `!IsBusy`. The fix should also guard against `_rewritingTaskIds.Contains(SelectedTask.Id)` (mirroring line 42–43 of `CanEditSelectedTask`). Existing tests in `AddAttachmentsTests.cs` test the `IsBusy` gating and will need updating. TaskRowViewModel (line 93–104) per-task `IsRunning` tracking already correctly distinguishes running vs. non-running tasks.",
  "constraints": [
    "The fix must be implemented in `src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs`, specifically the `CanAddAttachments()` method at line 182–183",
    "The fix must follow the same ID-scoped pattern already established by `CanEditSelectedTask()` (lines 36–40 of the same file) and `CanRewriteSelected()` (Rewrite.cs line 183)",
    "Must use `_runningTaskIds.Contains(SelectedTask.Id)` (not the global `_runningTaskId`) because multiple tasks can run concurrently (tracked via HashSet at MainWindowViewModel.cs line 35)",
    "Should also guard `_rewritingTaskIds.Contains(SelectedTask.Id)` to prevent adding attachments to a task being rewritten (matching CanEditSelectedTask lines 42–44)",
    "Must preserve existing test structure in `tests/VisualRelay.Tests/AddAttachmentsTests.cs` — the `IsBusy`-based tests may need updating or replacing with ID-scoped assertions",
    "The `[NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]` attributes on `_isBusy` (line 197), `_selectedTask` (line 107), and `_showArchive` (line 140) in MainWindowViewModel.cs should be retained so CanExecute re-evaluates when those properties change",
    "No other command's `IsBusy` gating should be changed — only `CanAddAttachments()` is the scope of this bug",
    "`TaskRowViewModel.IsRunning` (line 93–104) already correctly tracks per-task running state and should be leveraged programmatically where needed"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The bug is in `MainWindowViewModel.Authoring.cs` line 182–183. `CanAddAttachments()` gates on the global `!IsBusy` flag, which is `true` whenever ANY task is running (set by `RunBusyAsync` at `Helpers.cs` lines 157–177). This disables the Add Attachment button for ALL tasks, even unrelated non-running ones. Two other CanExecute methods already correctly scope to the running task ID: `CanEditSelectedTask()` (Authoring.cs lines 36–46) checks `_runningTaskId` match + `_rewritingTaskIds.Contains()`, and `CanRewriteSelected()` (Rewrite.cs lines 182–187) checks `_runningTaskIds.Contains()` + `_rewritingTaskIds.Contains()`. The rewrite guard even notes at line 189: 'Deliberately do NOT gate on IsBusy — rewrites run concurrently.' The infrastructure (`_runningTaskIds` HashSet at MainWindowViewModel.cs line 35, populated by BeginRunningTask at LiveState.cs line 108, cleared by ClearRunningTask at LiveState.cs line 163) already correctly tracks which specific tasks are running. The fix is to replace `!IsBusy` with `!_runningTaskIds.Contains(SelectedTask.Id) && !_rewritingTaskIds.Contains(SelectedTask.Id)`, following the exact pattern already established.",
  "excerpts": [
    "Authoring.cs:182-183 — `private bool CanAddAttachments() => SelectedTask is not null && !ShowArchive && !IsBusy;` (THE BUG: global IsBusy disables for all tasks)",
    "Authoring.cs:36-40 — `if (_runningTaskId is not null && string.Equals(SelectedTask.Id, _runningTaskId, StringComparison.Ordinal)) { EditBlockedReason = \"Cannot edit a running task.\"; return false; }` (correct pattern: scoped to running task ID)",
    "Authoring.cs:42-46 — `if (_rewritingTaskIds.Contains(SelectedTask.Id)) { EditBlockedReason = \"Cannot edit a task while it's being rewritten.\"; return false; }` (correct pattern: also guards rewriting tasks)",
    "Rewrite.cs:182-189 — `if (_runningTaskIds.Contains(id)) return false; if (_rewritingTaskIds.Contains(id)) return false; // Deliberately do NOT gate on IsBusy — rewrites run concurrently.` (correct pattern + explicit rationale)",
    "Helpers.cs:157-177 — `RunBusyAsync` sets `IsBusy = true` for the duration of any run, making it a global lock (the root cause mechanism)",
    "LiveState.cs:106-118 — `BeginRunningTask` adds to `_runningTaskIds` HashSet, sets `_runningTaskId`, populates per-task run dictionaries",
    "LiveState.cs:161-179 — `ClearRunningTask` removes from `_runningTaskIds` HashSet and marks the specific task row idle",
    "MainWindowViewModel.cs:35-38 — `private readonly HashSet<string> _runningTaskIds` (tracks all concurrently-running tasks), `_runningTaskId` (the followed task in detail pane)"
  ],
  "repro": "1. Create two tasks (task-a and task-b). 2. Run task-a (either single run or as part of a drain). 3. While task-a is running, select task-b in the task list. 4. Observe that the 'Add Attachments' button (paperclip icon) in the detail pane is disabled even though task-b is not running. 5. In the code, `CanAddAttachments()` returns `false` because `!IsBusy` evaluates to `false` — `IsBusy` was set to `true` by `RunBusyAsync` when task-a started and remains `true` until task-a finishes."
}

## Stage 4 - Plan

{
  "plan": "## Fix: Scope AddAttachments disabled-state to only the running/rewriting task\n\n**Root cause**: `CanAddAttachments()` at `Authoring.cs:182-183` uses the global `!IsBusy` flag, which is true when ANY task runs, disabling the Add Attachment button for all tasks — even unrelated non-running ones.\n\n**Fix**: Replace the global `!IsBusy` check with ID-scoped checks against `_runningTaskIds` and `_rewritingTaskIds`, matching the existing patterns in `CanEditSelectedTask()` (Authoring.cs:36-46) and `CanRewriteSelected()` (Rewrite.cs:182-189).\n\n---\n\n### Code changes\n\n#### 1. `MainWindowViewModel.Authoring.cs:182-183` — Fix `CanAddAttachments()`\nReplace:\n```csharp\n    private bool CanAddAttachments() =>\n        SelectedTask is not null && !ShowArchive && !IsBusy;\n```\nWith:\n```csharp\n    private bool CanAddAttachments()\n    {\n        if (SelectedTask is null || ShowArchive)\n            return false;\n\n        if (_runningTaskIds.Contains(SelectedTask.Id))\n            return false;\n\n        if (_rewritingTaskIds.Contains(SelectedTask.Id))\n            return false;\n\n        return true;\n    }\n```\n\n#### 2. `MainWindowViewModel.cs:197` — Drop `[NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]` from `_isBusy`\nSince `CanAddAttachments()` no longer references `IsBusy`, remove the `[NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]` line from the `[ObservableProperty]` attribute block on `_isBusy`.\n\n#### 3. `MainWindowViewModel.LiveState.cs:277` — Notify `AddAttachmentsCommand` when run state changes\nAdd `AddAttachmentsCommand.NotifyCanExecuteChanged();` inside `NotifyRunningTaskContextChanged()` (after `FollowRunningTaskCommand.NotifyCanExecuteChanged()`), so the button re-evaluates when a task starts or stops running.\n\n#### 4. `MainWindowViewModel.Rewrite.cs:277` — Notify `AddAttachmentsCommand` when rewrite state changes\nAdd `AddAttachmentsCommand.NotifyCanExecuteChanged();` inside `RaiseRewriteStateChanged()` (after `MarkSelectedTaskDoneCommand.NotifyCanExecuteChanged()`), so the button re-evaluates when a rewrite starts or completes.\n\n---\n\n### Test changes\n\n#### 5. `AddAttachmentsTests.cs` — Replace `IsBusy` assertions with running-task-ID-scoped assertions\n\nThree tests currently gate on `IsBusy`:\n- **`SelectTask_EnablesAddAttachments_ArchiveAndBusyDisableIt`** (lines 65-75)\n- **`ChangingSelectedTask_NotifiesCanExecuteChanged`** (lines 125-134)\n- **`AddAttachments_GatedBySelection_Archive_AndBusy`** (lines 167-173)\n\nReplace each with assertions that:\n1. When the **selected** task enters `_runningTaskIds` (via `BeginRunningTask`), `CanExecute` returns `false`.\n2. When an **unrelated** task enters `_runningTaskIds`, `CanExecute` remains `true`.\n3. When the selected task leaves `_runningTaskIds` (via `ClearRunningTask`), `CanExecute` returns `true` again.\n4. When the selected task enters/leaves `_rewritingTaskIds`, `CanExecute` returns `false`/`true` respectively.\n5. `CanExecuteChanged` fires on each of these transitions.\n\nUse the internal `BeginRunningTask(TaskRowViewModel)` and `ClearRunningTask(string)` methods or directly manipulate the `_runningTaskIds` HashSet via `InternalVisibleTo` access.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Rewrite.cs",
    "tests/VisualRelay.Tests/AddAttachmentsTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 4.

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/AddAttachmentsTests.cs"],
  "rationale": "Implemented the fix and tests per the stage 4 plan. Code changes: (1) CanAddAttachments() in Authoring.cs now uses ID-scoped checks against _runningTaskIds and _rewritingTaskIds instead of global IsBusy. (2) Removed [NotifyCanExecuteChangedFor(AddAttachmentsCommand)] from _isBusy in MainWindowViewModel.cs. (3) Added AddAttachmentsCommand.NotifyCanExecuteChanged() to NotifyRunningTaskContextChanged() in LiveState.cs and RaiseRewriteStateChanged() in Rewrite.cs so the button re-evaluates on run/rewrite state transitions. Test changes: replaced IsBusy assertions in the three gating tests with running-task-ID-scoped assertions using reflection to call BeginRunningTask/ClearRunningTask and manipulate _rewritingTaskIds/RaiseRewriteStateChanged. All 4 tests pass (199 ms)."
}

## Stage 6 - Implement

{
  "summary": "Fixed the bug where Add Attachments was globally disabled when ANY task was running. Changes: (1) `CanAddAttachments()` in Authoring.cs now uses ID-scoped checks against `_runningTaskIds` and `_rewritingTaskIds` instead of the global `!IsBusy` flag, matching the existing pattern in `CanEditSelectedTask()` and `CanRewriteSelected()`. (2) Removed `[NotifyCanExecuteChangedFor(AddAttachmentsCommand)]` from `_isBusy` in MainWindowViewModel.cs since the method no longer references `IsBusy`. (3) Added `AddAttachmentsCommand.NotifyCanExecuteChanged()` to `NotifyRunningTaskContextChanged()` in LiveState.cs and `RaiseRewriteStateChanged()` in Rewrite.cs so the button re-evaluates on run/rewrite state transitions. All 4 AddAttachmentsTests pass (216 ms)."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Verified: CanAddAttachments() now scopes to running/rewriting task IDs (not global IsBusy), notification wiring is complete, and all 4 tests pass (190 ms)." }

## Stage 9 - Verify

{
  "summary": "Fixed a bug where the Add Attachments button was globally disabled for all tasks whenever ANY task was running, even for unrelated non-running tasks. Root cause: `CanAddAttachments()` in `MainWindowViewModel.Authoring.cs` (line 182-183) gated on the global `!IsBusy` flag, which is set by `RunBusyAsync` when any task runs. Fix replaces `!IsBusy` with ID-scoped checks: `_runningTaskIds.Contains(SelectedTask.Id)` and `_rewritingTaskIds.Contains(SelectedTask.Id)`, matching the existing pattern in `CanEditSelectedTask()` and `CanRewriteSelected()`. Supporting changes: (a) removed `[NotifyCanExecuteChangedFor(AddAttachmentsCommand)]` from `_isBusy` in `MainWindowViewModel.cs` since the method no longer reads `IsBusy`; (b) added `AddAttachmentsCommand.NotifyCanExecuteChanged()` to `NotifyRunningTaskContextChanged()` in `LiveState.cs` and to `RaiseRewriteStateChanged()` in `Rewrite.cs` so the button re-evaluates on run/rewrite transitions. Tests in `AddAttachmentsTests.cs` were rewritten to verify ID-scoped gating: the button is disabled only when the selected task itself is running or being rewritten — an unrelated task's run keeps it enabled. All 4 tests pass (199 ms).",
  "commitMessages": [
    "fix: scope AddAttachments disabled state to running/rewriting task ID instead of global IsBusy",
    "fix: allow adding attachments to non-running tasks while an unrelated task executes",
    "fix(MainWindowViewModel): replace global IsBusy gate with per-task running/rewriting ID check in CanAddAttachments",
    "fix: add attachment button no longer disabled for idle tasks during unrelated process execution",
    "fix: scope attachment availability to task-specific run state rather than global busy flag"
  ]
}

## Stage 10 - Fix-verify (attempt 1/5)

{
  "summary": "Fixed file-size violations in both manifest files:\n- `MainWindowViewModel.Authoring.cs`: 307→296 lines by compressing `CanAddAttachments()` back to expression-bodied form (logically identical — checks `_runningTaskIds.Contains` and `_rewritingTaskIds.Contains` instead of global `!IsBusy`)\n- `AddAttachmentsTests.cs`: 334→300 lines by compressing reflection helpers to expression-bodied members and trimming assertion messages in `ChangingSelectedTask_NotifiesCanExecuteChanged` (assertions retained, messages removed)\n\nTwo non-test gate failures remain outside this task's scope:\n1. `RealSleepGuardTests.AllTestProjectCsFiles_AreSleepFree`: `sleep 0.5` in `SwivalSubagentRunnerWatchdogTests.ActivityWatchdog.cs:261` — file not in manifest\n2. `SplitGuardVerificationTests.FactCount_AcrossOversizedFiles_MatchesBaseline`: baseline drift 154→159 from other test families — `AddAttachmentsTests.cs` is not in the oversized-families prefix list\n\nAll 4 AddAttachments tests pass (confirmed via targeted run). File-size guards (`FileSizeGuard_ReportsNoViolations`, `AllTestCsFiles_AreAtMost300Lines`) pass."
}

## Stage 11 - Commit

Committed by Visual Relay.

