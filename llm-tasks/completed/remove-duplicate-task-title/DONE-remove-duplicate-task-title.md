# Remove Duplicate Task Title

Under the Markdown tab, a selected task's title renders twice in read-only mode: once as a bold heading bound to `SelectedTaskName`, and again as the `# Title` first line inside the `SelectedTaskMarkdown` body. Remove the duplicate so the title shows exactly once, while keeping the title editable.

## Current state (researched)

The read-only Markdown view in `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` (the `<Grid ... IsVisible="{Binding IsMarkdownReadOnly}">`) stacks two elements:

- a bold heading `<TextBlock Grid.Row="0" Text="{Binding SelectedTaskName}" FontSize="18" FontWeight="Bold" .../>`
- a `<ScrollViewer>` containing `<TextBlock Text="{Binding SelectedTaskMarkdown}" .../>`

`SelectedTaskMarkdown` holds the full file content, set in `SelectTaskAsync` as `SelectedTaskMarkdown = input.Markdown` (`MainWindowViewModel.Commands.cs`), so its first line is the `# Title`. `SelectedTaskName` is that same title extracted via `ExtractTitleFromMarkdown(input.Markdown, task.Id)` (`MainWindowViewModel.TaskName.cs`). Hence the title appears twice in read-only mode.

Editing already separates the two: `EditSelectedTask` calls `SplitMarkdownTitle(SelectedTaskMarkdown, SelectedTask.Id)` and splits into `EditTitleBuffer` (title) + `EditBuffer` (body, without the `# Title` line) — see `MainWindowViewModel.Authoring.cs`. The title is independently editable; the duplication is read-only-only.

`SelectedTaskName` is also used for rename detection in `SaveEditAsync`: `var titleChanged = !string.Equals(title, SelectedTaskName, ...)`. It must stay even if no longer displayed. No UI test asserts on the bold heading element; existing VM tests in `TaskDetailEditRenameTests.cs` assert the `SelectedTaskName`/`EditTitleBuffer` values and are unaffected by a view-only change.

## What to build

Direction (final): **remove the bold `SelectedTaskName` heading** from the read-only Markdown view. The title then renders once, as the `# Title` line inside the `SelectedTaskMarkdown` body. This is a single-element XAML deletion with no ViewModel, screenshots-tool, or binding changes — chosen over stripping the title out of the body (which would need a new body-only property and ripple into `tools/VisualRelay.Screenshots/Program.cs`, which seeds `SelectedTaskMarkdown` directly). The author delegated the choice; this is the minimal, zero-ripple option.

1. **Test first (TDD).** Add a headless UI test that fails before the fix. Mirror the harness in `TaskDetailRemoveButtonLayoutTests.cs` (`[Collection("Headless")]`, `[AvaloniaFact]`, `TestRepository.Create()` + `repo.WriteConfig`, `new MainWindowViewModel { RootPath = repo.Root }`, `await LoadInitialAsync()`, set `SelectedTask`, `await viewModel.LastSelectionLoad!`, `Dispatcher.UIThread.RunJobs()`, then `new MainWindow { DataContext = viewModel, Width = 900, Height = 900 }.Show()`). Seed a task whose markdown begins with `# Implement Feature X` (as in the `SelectedTaskName_PopulatedOnSelect` test). Select it, set `SelectedTabIndex = 0` (Markdown tab) so `IsMarkdownReadOnly` is true, then collect the visible `TextBlock`s under `window.GetVisualDescendants().OfType<TaskDetailPanel>().Single()` whose `Text` contains `"Implement Feature X"` and assert the count is exactly `1`. Before the fix this is `2` (the bold heading plus the body); the test drives the fix.
2. **Remove the duplicate.** In `TaskDetailPanel.axaml`, delete the bold `<TextBlock Grid.Row="0" Text="{Binding SelectedTaskName}" .../>` from the read-only Markdown `<Grid RowDefinitions="Auto,*" IsVisible="{Binding IsMarkdownReadOnly}">`. The now-empty `Auto` row collapses to zero height; leave the `ScrollViewer` (the `SelectedTaskMarkdown` body) as-is. Do not touch the edit view (`EditTitleBuffer`/`EditBuffer`) or the new-task view.
3. **Keep `SelectedTaskName` in the ViewModel.** Do not remove the property or its assignment in `SelectTaskAsync`; `SaveEditAsync` still relies on it for rename detection, and `TaskDetailEditRenameTests.cs` still asserts it.

## Done when

- Selecting a task and viewing the read-only Markdown tab shows the title exactly once (as the `# Title` line in the body), with no separate bold heading.
- The new UI test passes; `TaskDetailEditRenameTests.cs` and `TaskDetailEditRenameTests.Save.cs` still pass unchanged.
- Editing the title still works: entering edit mode populates `EditTitleBuffer` from the `# Title` line, and saving renames/saves as before.
- `./visual-relay check` passes (file-size guard, format, build, tests, screenshot render). `TaskDetailPanel.axaml` stays under 300 lines.
- Commit subject is a Conventional Commit, e.g. `fix: render task title once in markdown tab` (lowercase after the prefix, no trailing period, ≤72 chars; body of at most three `- ` bullets).
