# Remove dead CanExecute notification after un-gating task creation

Follow-up from the code review of `create-tasks-during-runs` (commit `9025ac1`). Pure
hygiene — no behavior change, no bug.

## Current state (researched)
`create-tasks-during-runs` dropped `&& !IsBusy` from `CanCreateNewTask`, so
`CreateNewTaskCommand.CanExecute` no longer depends on `IsBusy`. But
`src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` still decorates `_isBusy` with
`[NotifyCanExecuteChangedFor(nameof(CreateNewTaskCommand))]` (≈ line 178). That
notification is now a no-op for the create command (it re-raises `CanExecuteChanged` that
re-evaluates to the same value). And in `tests/VisualRelay.Tests/NewTaskAuthoringTests.cs`,
`ChangingNewTaskTitle_NotifiesCanExecuteChanged` (≈ lines 97–107) still asserts that
toggling `IsBusy` fires `CanExecuteChanged` for `CreateNewTaskCommand`, with a comment that
implies `IsBusy` still gates the command — misleading now.

## What to build
1. Remove `CreateNewTaskCommand` from the `[NotifyCanExecuteChangedFor(...)]` list on
   `_isBusy` (keep any OTHER commands that legitimately depend on `IsBusy` — verify each;
   do NOT remove notifications for Run/Drain/Refresh/CreateConfig/AddAttachments/turn-budget
   which still gate on `!IsBusy`).
2. Update `ChangingNewTaskTitle_NotifiesCanExecuteChanged`: drop the stale `IsBusy`→create
   assertions (or move them to whichever command still depends on `IsBusy`) and fix the
   misleading comment so it reflects that title — not busy — drives the create command.
3. `./visual-relay check` green.

## Decisions (settled)
- Hygiene only; verify with a search which commands actually read `IsBusy` in their
  `CanExecute` before trimming any notification target. Do not over-trim.
