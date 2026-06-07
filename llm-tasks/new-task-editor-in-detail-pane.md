# New-task authoring is a cramped modal and "Create task" never enables

Creating a task today happens in a tiny floating card crammed into the 280px-wide QUEUE
column. The body editor is a fixed `Height="120"` box only a few lines tall
(`src/VisualRelay.App/Views/Controls/QueuePanel.axaml:198`), so writing anything longer than a
sentence means scrolling a postage-stamp textarea — even though the whole middle TASK pane
sits empty, with a full-height editor already used for *editing* existing task bodies.

Worse, the dialog's **Create task** button is dead: it never enables even when a title is
typed. The command gate is fine —

```csharp
// src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs
private bool CanCreateNewTask() =>
    !string.IsNullOrWhiteSpace(NewTaskTitle) && !IsBusy;
```

— but the properties it depends on never tell the command to re-check. `NewTaskTitle`
(`MainWindowViewModel.cs:161`) and `IsBusy` (`:176`) are declared `[ObservableProperty]`
**without** `[NotifyCanExecuteChangedFor(nameof(CreateNewTaskCommand))]`. So `CanExecute` is
evaluated once when the dialog opens (title empty → disabled) and is never re-evaluated as the
user types, leaving the button permanently greyed out. Contrast `InitTestCommandInput`
(`:104-106`), which *does* carry `[NotifyCanExecuteChangedFor(nameof(CreateConfigCommand))]` —
which is exactly why the analogous "Create config" button works.

## Recommended fix

Stop authoring new tasks in the QUEUE-column modal. Instead, run new-task creation through the
middle `TaskDetailPanel`, reusing the same generous space and layout the **Edit** flow already
uses, so creating and editing a task feel identical.

Concretely:

- **Move authoring into `TaskDetailPanel.axaml`'s Markdown tab.** Add a "new task" mode that
  mirrors the existing edit mode (`TaskDetailPanel.axaml:104-160`): a title `TextBox` (bound to
  `NewTaskTitle`, the only field edit-mode lacks) above a **full-height** body editor bound to
  `NewTaskBody` — same `AcceptsReturn`, monospace font, colors, and `Grid.Row="1"` stretch as
  the existing `EditBuffer` TextBox — with **Create task** / **Cancel** buttons in the same
  toolbar slot as Save/Cancel, and the `NewTaskError` message shown inline. Drive visibility
  with `IsNewTaskDialogOpen` (rename if a clearer name fits), the way `IsEditingMarkdown`
  already toggles the read-only view vs. the edit TextBox. The read-only markdown view and the
  Edit toolbar should hide while authoring, so the pane shows one thing at a time.
- **Remove the floating New Task `Border` from `QueuePanel.axaml`** (`:193-220` "New Task
  Dialog"). Keep the **New** button in the queue header (`QueuePanel.axaml:27`) — clicking it
  still calls `OpenNewTaskDialogCommand`, but now it reveals the authoring view in the detail
  pane instead of the cramped card.
- **Fix the dead button.** Add `[NotifyCanExecuteChangedFor(nameof(CreateNewTaskCommand))]` to
  both `NewTaskTitle` and `IsBusy` so the command re-evaluates as the user types and as busy
  state changes. After this, typing a title must enable **Create task**.
- Preserve all existing behavior: `OpenNewTaskDialog` toggling, slug derivation/validation,
  `NewTaskError` surfacing, and selecting the newly created task on success
  (`CreateNewTaskAsync`) must all still work. Cancel must clear the in-progress title/body and
  return the pane to the normal read-only view.
- Mind interaction with `IsEditingMarkdown`: opening new-task authoring while editing an
  existing task (or vice versa) must not show both editors at once — entering one mode exits the
  other.

## Done when

- Clicking **New** opens a full-size task editor in the middle TASK pane (title + full-height
  body), not a small card in the QUEUE column; the old floating dialog is gone.
- The body editor uses the same generous height/styling as the Edit-task editor — long bodies
  no longer scroll a ~120px box.
- Typing a title enables **Create task**; clicking it creates the task, closes the editor,
  refreshes the queue, and selects the new task. Cancel discards input and restores the
  read-only view.
- A headless UI/view-model regression test covers the previously-broken path: open new-task
  authoring, set a title, assert `CreateNewTaskCommand.CanExecute` is true and the task is
  created (mirror the existing authoring/init tests, e.g. `ConfigInitEmptyStateUiTests` /
  `MainWindowViewModelTests`).
- Verify with `./visual-relay screenshot`.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
