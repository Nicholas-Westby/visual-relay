# Allow creating tasks during an active run

The **"Create task"** button is disabled while Visual Relay is running a task (or draining the
queue), so you can't author a new task until the run finishes. It should be possible to create
LLM tasks even during a run. (The user hit this with the authoring form already open mid-run — the
"New" button worked, but "Create task" was greyed out.)

## Current state (researched)

> **Freshness contract.** Verify every reference below by searching for the quoted string, not by
> line number; re-read the file if a snippet has drifted.

**The gate is `!IsBusy` on the create command.** `src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs`:

```csharp
[RelayCommand(CanExecute = nameof(CanCreateNewTask))]
private async Task CreateNewTaskAsync() { … }

private bool CanCreateNewTask() =>
    !string.IsNullOrWhiteSpace(NewTaskTitle) && !IsBusy;
```

`IsBusy` is `true` for the whole duration of a run/drain (set in `RunBusyAsync`,
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs`). The "Create task" button in
`src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` binds `Command="{Binding CreateNewTaskCommand}"`,
so it disables during runs. **This `&& !IsBusy` is the bug.**

**Opening the form already works mid-run.** The "New" button (`OpenNewTaskDialogCommand`,
`QueuePanel.axaml` + `OpenNewTaskDialog()` in `Authoring.cs`) is a plain `[RelayCommand]` with no
`CanExecute`, so the authoring pane already opens during a run (matches the screenshot). Only the
final create action is blocked.

**PITFALL — the refresh at the end of create no-ops while busy.** `CreateNewTaskAsync` finishes
with `await RefreshAsync();`. `RefreshAsync` (`MainWindowViewModel.Commands.cs`) runs inside
`RunBusyAsync`, which **early-returns when `IsBusy` is true**:

```csharp
private async Task RunBusyAsync(Func<Task> action)
{
    if (IsBusy) { return; }   // ← refresh silently skipped during a run
    …
}
```

So if you *only* remove the gate, the file is written but the list never reloads and the new task
won't appear or get selected until the run ends.

**The non-gated reload to use instead: `ReloadTaskListAsync`.**
`MainWindowViewModel.Helpers.cs` defines `private async Task ReloadTaskListAsync(string? preferredTaskId = null)`
— it is **not** wrapped in `RunBusyAsync`, rebuilds `Tasks`, re-applies the running row
(`ApplyRunningTaskToRows()`), and selects `preferredTaskId`. It's already what the attachment and
edit paths call to reload while preserving selection (`await ReloadTaskListAsync(currentTask.Id);`
in `Authoring.cs`).

**Creating a task does NOT commit — the run's commit-gate hook is irrelevant.**
`src/VisualRelay.Core/Tasks/RelayTaskWriter.cs` `CreateAsync` only writes the markdown file in the
nested subfolder form (`llm-tasks/<slug>/<slug>.md`); there is no `git` call. So the pre-commit
"no commits during an active Relay run" hook never fires here. The backend also already tolerates
task files appearing mid-run (see `llm-tasks/DONE-tolerate-task-files-added-mid-run.md`).

## What to build

1. **Drop the `!IsBusy` gate** from `CanCreateNewTask` — keep the title-required check:
   `private bool CanCreateNewTask() => !string.IsNullOrWhiteSpace(NewTaskTitle);`
   (`NewTaskTitle` already notifies `CreateNewTaskCommand` via `[NotifyCanExecuteChangedFor]`.)
2. **Make the post-create refresh work while busy:** in `CreateNewTaskAsync`, replace
   `await RefreshAsync();` with `await ReloadTaskListAsync(slug);`. That reloads the list and
   selects the new task even when `IsBusy` is true. The trailing manual
   `SelectedTask = Tasks.FirstOrDefault(... slug ...)` is then redundant (the reload already
   selects `slug`) — remove it or leave it (harmless).
3. Leave `OpenNewTaskDialog`, editing, Run/Drain, etc. unchanged — see scope decision below.
4. Keep all collection mutations on the UI thread (already the case).

## Tests / verification (TDD — write/flip the failing test first)

- **Flip the existing busy-gate test.** `tests/VisualRelay.Tests/NewTaskAuthoringTests.cs` →
  `NewTaskTitle_WhitespaceOrEmpty_KeepsCreateDisabled_BusyAlsoDisables` currently asserts:
  ```csharp
  // Valid title but busy → disabled.
  viewModel.IsBusy = true;
  Assert.False(viewModel.CreateNewTaskCommand.CanExecute(null));
  // No longer busy → enabled again.
  viewModel.IsBusy = false;
  Assert.True(viewModel.CreateNewTaskCommand.CanExecute(null));
  ```
  After the fix, a valid title + `IsBusy = true` must be **enabled**. Invert those assertions and
  rename the test (drop "BusyAlsoDisables"; e.g. `…CreateEnabledEvenWhenBusy`). Keep the
  empty/whitespace-title cases (still disabled).
- **Add a create-while-busy test:** with `IsBusy = true` and a valid `NewTaskTitle`,
  `CreateNewTaskCommand.CanExecute(null)` is `true`; awaiting `CreateNewTaskCommand.ExecuteAsync(null)`
  writes the task and it appears in `Tasks` (and is the `SelectedTask`) while `IsBusy` is still
  `true`. Use `TestRepository` like the sibling tests.
- `./visual-relay check` green.

## Decisions (settled)

1. **Scope is task CREATION only.** Editing an existing task, Run selected, Drain, Create config,
   and the turn-budget toggle stay `!IsBusy`-gated — do **not** loosen those. *Why:* the user asked
   specifically to create tasks during runs; creating a brand-new file is safe (no commit, backend
   tolerates mid-run additions), whereas mutating/running a task that may be in flight is a
   different risk.
2. **Use `ReloadTaskListAsync`, not `RefreshAsync`, in the create path.** *Why:* `RefreshAsync`
   no-ops while busy (`RunBusyAsync` early-returns), so the new task would be invisible until the
   run ends.
3. **No commit on create.** *Why:* `CreateAsync` only writes the file; the active-run commit-gate
   hook is not involved.
