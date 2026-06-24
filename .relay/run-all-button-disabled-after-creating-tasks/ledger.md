## Stage 1 - Ideate

{ "summary": "The 'Run All' button's disabled state is correctly initialized (true when no tasks) but never re-evaluated after task creation. Fix requires ensuring the button re-derives its disabled state reactively from the current task list.", "options": ["Option A — Add missing reactive dependency: find the computed/memo/effect that drives `isDisabled` and add `tasks` to its dependency array (e.g., `useMemo(() => tasks.length === 0, [tasks])` instead of `[]`). This is the simplest fix if the plumbing is already correct.", "Option B — Lift state to a reactive store: move the 'has tasks' check into a global store (Zustand/Pinia/Redux) where mutations automatically notify subscribers. The button subscribes to a derived `hasTasks` selector. Guards against similar bugs anywhere else the check is needed.", "Option C — Explicit invalidation after creation: have the task-creation handler emit a callback or event that forces the button to re-evaluate its disabled state (e.g., `setHasTasks(true)` directly, or call a refresh function). Least elegant but most surgical."] }

## Stage 2 - Research

{
  "findings": "The 'Run All' button in TopBar.axaml (line 128-129) is bound to `DrainQueueCommand`, declared in Execution.cs (line 56) with `[RelayCommand(CanExecute = nameof(CanDrain))]`. The `CanDrain()` method (Helpers.cs line 204) returns `!IsBusy && !PauseRequested && !ShowArchive && Tasks.Any(task => !task.NeedsReview)`. The `Tasks` collection (MainWindowViewModel.cs line 75) is a plain `ObservableCollection<TaskRowViewModel>`, not an `[ObservableProperty]`, so mutations to it never trigger `NotifyCanExecuteChangedFor(DrainQueueCommand)`. Four `[ObservableProperty]` fields do carry `[NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]` — `_pauseRequested`, `_rootPath`, `_showArchive`, and `_isBusy` — but none of those change during task creation. The central `ReloadTaskListAsync()` (Helpers.cs lines 115-154) clears and repopulates `Tasks` but never calls `DrainQueueCommand.NotifyCanExecuteChanged()`. All task-list-modifying paths route through `ReloadTaskListAsync()`: `CreateNewTaskAsync` (Authoring.cs:253), `AddAttachmentsAsync` (Authoring.cs:137), `RemoveAttachmentAsync` (Authoring.cs:170), `RefreshAsync` (Commands.cs:29), `ToggleArchiveAsync` (Commands.cs:101), `FollowRunningTaskAsync` (Commands.cs:86,93), `RefreshTasksAfterDrainAsync` (Helpers.cs:111), `RunSelectedAsync` (Execution.cs:31), and `ResumeSelectedAsync` (Execution.cs:52). The fix is to add `DrainQueueCommand.NotifyCanExecuteChanged()` at the end of `ReloadTaskListAsync()`, after the `SelectedTask` assignment (after line 153). This single-site change corrects all paths. The prior stage's stage5-attempt1 already authored a test `DrainQueueCommand_FiresCanExecuteChanged_AfterCreatingTask` in `MainWindowViewModelTests.cs` that subscribes to `CanExecuteChanged` on `DrainQueueCommand`, creates a task via `CreateNewTaskCommand`, and asserts the event fired. That test will pass once the fix is applied. Additionally, `RunSelectedCommand` is also notified through `SelectedTask` changes (line 104 of MainWindowViewModel.cs) and `IsBusy`/`PauseRequested`/`ShowArchive` changes, so it does not share this bug.",
  "constraints": [
    "The `Tasks` field (line 75 of MainWindowViewModel.cs) is a plain `ObservableCollection` — cannot add `[NotifyCanExecuteChangedFor]` since it isn't an `[ObservableProperty]`",
    "The fix must go in `ReloadTaskListAsync()` in Helpers.cs to cover all mutation paths (create, edit, attach, remove, refresh, archive toggle, drain, run/resume)",
    "`RunSelectedCommand` does NOT share this bug — its `CanRunSelected()` depends on `SelectedTask`, `IsBusy`, `PauseRequested`, `ShowArchive`, all of which are `[ObservableProperty]` with proper `[NotifyCanExecuteChangedFor]`",
    "The MVVM Community Toolkit source generator only auto-wires `[NotifyCanExecuteChangedFor]` on `[ObservableProperty]` fields — it does not observe `ObservableCollection.CollectionChanged`",
    "Existing test `TogglePauseCommand_ShowsTaskBoundarySemanticsAndBlocksNewRuns` in MainWindowViewModelTests.cs already asserts `DrainQueueCommand.CanExecute(null)` after manually adding a task to `viewModel.Tasks` (line 90-91), but this bypasses `ReloadTaskListAsync` so it never revealed the bug",
    "Existing test `ToggleArchiveCommand_LoadsCompletedTasksAndDisablesRunActions` asserts `DrainQueueCommand.CanExecute(null)` is false after toggling to archive, which works because `_showArchive` has `[NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]`",
    "The prior stage already authored a test `DrainQueueCommand_FiresCanExecuteChanged_AfterCreatingTask` in MainWindowViewModelTests.cs that verifies the fix",
    "Project uses .NET 10, Avalonia, CommunityToolkit.Mvvm, xUnit v3"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The 'Run All' button in TopBar.axaml:128-129 binds to DrainQueueCommand. That command's CanExecute delegate calls CanDrain() (Helpers.cs:204), which returns `!IsBusy && !PauseRequested && !ShowArchive && Tasks.Any(task => !task.NeedsReview)`. The `Tasks` field (MainWindowViewModel.cs:75) is a plain `ObservableCollection<TaskRowViewModel>` — not an `[ObservableProperty]` — so the CommunityToolkit.Mvvm source generator cannot attach `[NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]` to it. Four `[ObservableProperty]` fields do have that attribute (`_pauseRequested`, `_rootPath`, `_showArchive`, `_isBusy`), but none of them change during task creation. All task-creation/mutation paths flow through `ReloadTaskListAsync()` (Helpers.cs:115-154), which clears and repopulates `Tasks` but never calls `DrainQueueCommand.NotifyCanExecuteChanged()`. A grep of the entire source tree confirms `DrainQueueCommand.NotifyCanExecuteChanged` is never called anywhere. The existing test `TogglePauseCommand_ShowsTaskBoundarySemanticsAndBlocksNewRuns` (MainWindowViewModelTests.cs:87-95) asserts `CanExecute(null)` returns the correct value, but it calls the delegate directly — it never verifies the `CanExecuteChanged` event fires, which is what actually notifies the Avalonia binding to re-read the enabled state. The fix is a single-line addition of `DrainQueueCommand.NotifyCanExecuteChanged()` at the end of `ReloadTaskListAsync()`.",
  "excerpts": [
    "TopBar.axaml:128-129: Command=\"{Binding DrainQueueCommand}\" Content=\"Run All\" — the button's IsEnabled is driven by CanExecute/CanExecuteChanged on DrainQueueCommand",
    "Execution.cs:56-57: [RelayCommand(CanExecute = nameof(CanDrain))] private async Task DrainQueueAsync() — the command whose CanExecute delegates to CanDrain()",
    "Helpers.cs:204: private bool CanDrain() => !IsBusy && !PauseRequested && !ShowArchive && Tasks.Any(task => !task.NeedsReview); — depends on Tasks being non-empty",
    "MainWindowViewModel.cs:75: public ObservableCollection<TaskRowViewModel> Tasks { get; } = []; — plain collection, not [ObservableProperty], so source generator cannot add [NotifyCanExecuteChangedFor]",
    "MainWindowViewModel.cs:81-90: [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))] private bool _pauseRequested; — _pauseRequested has the attribute, but doesn't change during task creation",
    "MainWindowViewModel.cs:92-101: [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))] private string _rootPath; — same pattern, doesn't change on task creation",
    "MainWindowViewModel.cs:137-143: [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))] private bool _showArchive; — same",
    "MainWindowViewModel.cs:192-198: [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))] private bool _isBusy; — same",
    "Helpers.cs:115-154: ReloadTaskListAsync() clears and repopulates Tasks but never calls DrainQueueCommand.NotifyCanExecuteChanged() — the missing notification",
    "Authoring.cs:253: await ReloadTaskListAsync(slug); — CreateNewTaskAsync ends here, flowing through the same gap",
    "Commands.cs:29: await ReloadTaskListAsync(); — RefreshAsync flows through the same gap",
    "grep result: DrainQueueCommand.NotifyCanExecuteChanged is never called anywhere in src/",
    "MainWindowViewModelTests.cs:87-95: TogglePauseCommand test calls CanExecute(null) directly and passes, but never subscribes to CanExecuteChanged — the test bypasses the bug"
  ],
  "repro": "1. Open Visual Relay and select a project with no tasks → 'Run All' is correctly disabled.\n2. Create a new task (e.g., via the New Task dialog) → `CreateNewTaskAsync` calls `ReloadTaskListAsync` which populates `Tasks`.\n3. After `ReloadTaskListAsync` returns, `Tasks` now contains the new task, so `CanDrain()` would return `true` if called.\n4. But `DrainQueueCommand.NotifyCanExecuteChanged()` was never called, so the Avalonia binding never re-queries `CanExecute`. The button remains disabled.\n5. Manually toggling the Archive toggle or Pause button would call `NotifyCanExecuteChanged` (via `[NotifyCanExecuteChangedFor]` on `_showArchive` / `_pauseRequested`), which would re-enable the button — but no such side-effect occurs during normal task creation."
}

## Stage 4 - Plan

{
  "plan": "## Fix (1 line)\n\n**File: `src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs`**\n\nIn `ReloadTaskListAsync()` (line 154 is the closing brace), insert after line 153:\n\n```csharp\nDrainQueueCommand.NotifyCanExecuteChanged();\n```\n\nThis ensures every path that mutates the task list — create, edit, attach, remove, refresh, archive toggle, drain, run/resume — fires `CanExecuteChanged` so the Avalonia Button binding re-reads `CanExecute` and enables the \"Run All\" button when tasks exist.\n\n## Test (new method)\n\n**File: `tests/VisualRelay.Tests/MainWindowViewModelTests.cs`**\n\nAdd a new `[Fact]` method `DrainQueueCommand_FiresCanExecuteChanged_AfterCreatingTask` that:\n1. Sets up a test repo with a valid config and one existing task.\n2. Loads the ViewModel, opens the new-task dialog, sets a title.\n3. Subscribes to `DrainQueueCommand.CanExecuteChanged` with a counter.\n4. Executes `CreateNewTaskCommand.ExecuteAsync(null)`.\n5. Asserts the counter >= 1 (the event fired), proving the button would re-read `CanExecute` and become enabled.\n\nFollows the same event-subscription pattern used by `ChangingNewTaskTitle_NotifiesCanExecuteChanged` and `ChangingSelectedTask_NotifiesCanExecuteChanged`.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs",
    "+tests/VisualRelay.Tests/MainWindowViewModelTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/MainWindowViewModelTests.cs"
  ],
  "rationale": "The test `DrainQueueCommand_FiresCanExecuteChanged_AfterCreatingTask` subscribes to `DrainQueueCommand.CanExecuteChanged` after `LoadInitialAsync` completes (avoiding IsBusy-toggle noise), then creates a task via `CreateNewTaskCommand.ExecuteAsync(null)`. This flows through `ReloadTaskListAsync`, which currently never calls `DrainQueueCommand.NotifyCanExecuteChanged()`. The test asserts the event counter is >= 1, proving the 'Run All' button's binding would re-read `CanExecute`. The test compiles and fails as expected — `ReloadTaskListAsync` does not yet call `NotifyCanExecuteChanged`, so the counter remains 0. Once the one-line fix (`DrainQueueCommand.NotifyCanExecuteChanged()` at the end of `ReloadTaskListAsync`) is applied, the test will pass."
}

## Stage 6 - Implement

{ "summary": "Added `DrainQueueCommand.NotifyCanExecuteChanged();` at the end of `ReloadTaskListAsync()` in `MainWindowViewModel.Helpers.cs` (after line 153). This ensures every path that mutates the task list — create, edit, attach, remove, refresh, archive toggle, drain, run/resume — fires `CanExecuteChanged`, so the Avalonia Button binding re-reads `CanExecute` and enables the 'Run All' button when tasks exist. The existing test `DrainQueueCommand_FiresCanExecuteChanged_AfterCreatingTask` now passes, confirming the fix." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Verified: DrainQueueCommand.NotifyCanExecuteChanged() added at end of ReloadTaskListAsync() (Helpers.cs line 154) fixes the 'Run All' button remaining disabled after task creation. All 1,892 tests pass, including the targeted DrainQueueCommand_FiresCanExecuteChanged_AfterCreatingTask test." }

## Stage 9 - Verify

{
  "summary": "Fixed 'Run All' button staying disabled after creating tasks. The root cause was that `ReloadTaskListAsync()` (Helpers.cs) cleared and repopulated the `Tasks` collection — which drives `CanDrain()` — but never notified `DrainQueueCommand` that its `CanExecute` state had changed. Since `Tasks` is a plain `ObservableCollection` (not an `[ObservableProperty]`), the CommunityToolkit.Mvvm source generator could not attach `[NotifyCanExecuteChangedFor]` to it. The one-line fix adds `DrainQueueCommand.NotifyCanExecuteChanged();` at line 154 of `MainWindowViewModel.Helpers.cs`, after the task list is fully rebuilt. This covers all mutation paths (create, edit, attach, remove, refresh, archive toggle, drain, run/resume) since they all route through `ReloadTaskListAsync()`. The targeted test `DrainQueueCommand_FiresCanExecuteChanged_AfterCreatingTask` subscribes to `CanExecuteChanged` and asserts the event fires after creating a task — confirming the Avalonia binding will re-read `CanExecute` and enable the button. All 1,892 tests pass.",
  "commitMessages": [
    "fix: notify DrainQueueCommand.CanExecuteChanged after reloading task list",
    "fix: enable 'Run All' button by re-evaluating CanDrain after task creation",
    "fix: add missing NotifyCanExecuteChanged call in ReloadTaskListAsync"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

