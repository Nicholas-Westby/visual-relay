## Stage 1 - Ideate

{ "summary": "The Add Attachments button is permanently disabled because AddAttachmentsCommand's CanExecute depends on SelectedTask, ShowArchive, and IsBusy, but none of those properties notify the command when they change — the command never re-evaluates. The fix is to add [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))] to the three observable properties, and optionally add an explicit .NotifyCanExecuteChanged() call in OnSelectedTaskChanged (matching the pattern already used for EditSelectedTaskCommand).", "options": ["Option A — Add missing [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))] attributes on _selectedTask, _showArchive, and _isBusy.", "Option B — Same as A plus AddAttachmentsCommand.NotifyCanExecuteChanged() in OnSelectedTaskChanged for consistency with the Edit button.", "Option C — Same as B plus add a CanExecute guard on RemoveAttachmentCommand for consistency."] }

## Stage 2 - Research

{
  "findings": "The Add Attachments button stays disabled because `AddAttachmentsCommand` never re-evaluates its `CanExecute` state. The root cause is that three observable properties — `_selectedTask` (MainWindowViewModel.cs:92-99), `_showArchive` (MainWindowViewModel.cs:126-133), and `_isBusy` (MainWindowViewModel.cs:179-186) — all lack `[NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]` attributes. The command's `CanAddAttachments()` method (Authoring.cs:118-119) requires `SelectedTask is not null && !ShowArchive && !IsBusy`, but the button remains in its initial disabled state because none of these property changes trigger a re-query. Additionally, `OnSelectedTaskChanged()` (Commands.cs:134-162) calls `EditSelectedTaskCommand.NotifyCanExecuteChanged()` on line 160 for consistency, but does not similarly notify `AddAttachmentsCommand`. An identical bug pattern was previously fixed for `CreateNewTaskCommand` (documented with comments in NewTaskAuthoringTests.cs).",
  "constraints": [
    "The codebase uses CommunityToolkit.Mvvm v8+ source generators; [NotifyCanExecuteChangedFor] is the idiomatic mechanism to auto-notify CanExecute.",
    "NullFilePicker returns an empty list, so AddAttachmentsAsync would early-return in test/headless scenarios (no side effects).",
    "ShowConfirmationAsync can be null in headless/test scenarios, skipping the confirmation prompt (harmless for this fix).",
    "The project targets .NET 10 / Avalonia UI; no unusual platform constraints."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The AddAttachmentsCommand CanExecute gate (Authoring.cs:118-119) requires `SelectedTask is not null && !ShowArchive && !IsBusy`. All three observable properties — _selectedTask (MainWindowViewModel.cs:92-99), _showArchive (:126-133), _isBusy (:179-186) — lack [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))], so property changes never trigger CanExecute re-evaluation. Additionally, OnSelectedTaskChanged (Commands.cs:160) calls EditSelectedTaskCommand.NotifyCanExecuteChanged() but omits AddAttachmentsCommand. This is the identical bug pattern previously fixed for CreateNewTaskCommand, documented in NewTaskAuthoringTests.cs:8-14.",
  "excerpts": [
    "Authoring.cs:80: [RelayCommand(CanExecute = nameof(CanAddAttachments))]",
    "Authoring.cs:118-119: private bool CanAddAttachments() => SelectedTask is not null && !ShowArchive && !IsBusy;",
    "MainWindowViewModel.cs:91-99: [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ToggleArchiveCommand))] [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))] [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))] [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))] [NotifyCanExecuteChangedFor(nameof(FollowRunningTaskCommand))] private TaskRowViewModel? _selectedTask;  // ← missing AddAttachmentsCommand",
    "MainWindowViewModel.cs:126-133: [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))] [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))] [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))] [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))] private bool _showArchive;  // ← missing AddAttachmentsCommand",
    "MainWindowViewModel.cs:179-186: [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ToggleArchiveCommand))] [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))] [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))] [NotifyCanExecuteChangedFor(nameof(CreateConfigCommand))] [NotifyCanExecuteChangedFor(nameof(CreateNewTaskCommand))] private bool _isBusy;  // ← missing AddAttachmentsCommand",
    "Commands.cs:160: EditSelectedTaskCommand.NotifyCanExecuteChanged();  // AddAttachmentsCommand never notified here",
    "NewTaskAuthoringTests.cs:8-14: 'The bug was that [NotifyCanExecuteChangedFor] was missing on NewTaskTitle and IsBusy, so the Create button never enabled when the user typed.' — identical pattern"
  ],
  "repro": "1. Start the app with a valid tasks directory. 2. Select any task from the QUEUE list. 3. Observe that the 'Add Attachments' button in the TASK detail pane remains disabled (greyed out) regardless of task selection, archive state, or busy state — it never becomes clickable."
}

## Stage 4 - Plan

{
  "plan": "## Fix: AddAttachmentsCommand never re-evaluates CanExecute\n\nThe `CanAddAttachments()` gate (Authoring.cs:118-119) requires `SelectedTask is not null && !ShowArchive && !IsBusy`, but the three observable properties never notify `AddAttachmentsCommand` when they change. Fix in 4 edits across 2 files, plus a regression test:\n\n### Edit 1 — MainWindowViewModel.cs:96 (before line 97)\nInsert `[NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]` on the `_selectedTask` property, after the existing `FollowRunningTaskCommand` attribute on line 96.\n\n### Edit 2 — MainWindowViewModel.cs:130 (before line 131)\nInsert `[NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]` on the `_showArchive` property, after the existing `MoveDownCommand` attribute on line 130.\n\n### Edit 3 — MainWindowViewModel.cs:184 (before line 185)\nInsert `[NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]` on the `_isBusy` property, after the existing `CreateNewTaskCommand` attribute on line 184.\n\n### Edit 4 — MainWindowViewModel.Commands.cs:160 (after line 160)\nAdd `AddAttachmentsCommand.NotifyCanExecuteChanged();` right after `EditSelectedTaskCommand.NotifyCanExecuteChanged();` in `OnSelectedTaskChanged`, matching the existing consistency pattern.\n\n### Edit 5 — New test file: tests/VisualRelay.Tests/AddAttachmentsTests.cs\nWrite a headless AvaloniaFact test (mirroring `NewTaskAuthoringTests.cs` pattern) that:\n1. Creates a ViewModel with a temp repo containing one task.\n2. Asserts `AddAttachmentsCommand.CanExecute(null)` is `false` before selection.\n3. Sets `SelectedTask` to the first task row.\n4. Dispatches `UIThread.RunJobs()`.\n5. Asserts `AddAttachmentsCommand.CanExecute(null)` is `true`.\n6. Sets `ShowArchive = true` and asserts it becomes `false`.\n7. Sets `IsBusy = true` and asserts it becomes `false`.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs",
    "tests/VisualRelay.Tests/AddAttachmentsTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/AddAttachmentsTests.cs"
  ],
  "rationale": "Four tests in AddAttachmentsTests.cs. Three pass (behavioral smoke tests that verify the CanAddAttachments predicate logic and NullFilePicker safety) and exactly one fails: ChangingSelectedTask_NotifiesCanExecuteChanged. That test subscribes to AddAttachmentsCommand.CanExecuteChanged before LoadInitialAsync, verifies zero notifications during load (proving [NotifyCanExecuteChangedFor] is missing on _selectedTask, _showArchive, _isBusy), then asserts the event fires when each property changes — it fails on the first property-change assertion because the missing attributes mean CanExecuteChanged never fires. This is the correct RED state: the test proves the exact bug described in the diagnosis, and will go GREEN once the three [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))] attributes are added in the implementation stage."
}

## Stage 6 - Implement

{ "summary": "Applied four edits across two files to fix the permanently-disabled Add Attachments button. Added [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))] to _selectedTask (line 97), _showArchive (line 132), and _isBusy (line 187) in MainWindowViewModel.cs, mirroring the pattern already used for other commands on those same properties. Added AddAttachmentsCommand.NotifyCanExecuteChanged() in OnSelectedTaskChanged (Commands.cs line 161) for consistency with EditSelectedTaskCommand. All 4 AddAttachmentsTests pass green." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Fix verified. All three observable properties now carry [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))], and OnSelectedTaskChanged explicitly calls AddAttachmentsCommand.NotifyCanExecuteChanged(). Four AddAttachmentsTests pass green — the Add Attachments button is now properly enabled when a task is selected, not in archive view, and not busy." }

## Stage 9 - Verify

{
  "summary": "Added [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))] to the _selectedTask, _showArchive, and _isBusy observable properties in MainWindowViewModel.cs, plus an explicit AddAttachmentsCommand.NotifyCanExecuteChanged() call in OnSelectedTaskChanged for consistency with the existing Edit button pattern. All 4 AddAttachmentsTests pass green.",
  "commitMessages": [
    "fix: enable AddAttachments button by wiring CanExecuteChanged for all three guard properties",
    "fix: add missing NotifyCanExecuteChangedFor attributes on SelectedTask, ShowArchive, and IsBusy",
    "fix: Add Attachments button now properly activates when a task is selected",
    "fix: notify AddAttachmentsCommand on property changes that gate its CanExecute"
  ]
}

## Stage 10 - Fix-verify

{ "summary": "Fix verified. All three observable properties already carry [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))], and OnSelectedTaskChanged explicitly calls AddAttachmentsCommand.NotifyCanExecuteChanged(). All 4 AddAttachmentsTests pass green — the Add Attachments button is now properly enabled when a task is selected, not in archive view, and not busy." }

## Stage 11 - Commit

Committed by Visual Relay.

