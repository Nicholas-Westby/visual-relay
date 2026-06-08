## Stage 1 - Ideate

{
  "summary": "The 'New' button creates a task but fails to reset the active tab to 'Markdown' when the user is on the 'Attachments' tab. The first click works because the default tab is Markdown, but after switching to Attachments and clicking 'New', the new task is created yet invisible because the UI stays on the Attachments tab. Additionally, diagnostic output shows `traceList is null` / `ItemsPanelRoot is null`, suggesting the underlying items control binding may also be fragile when tabs change.",
  "options": [
    "Force tab reset to 'Markdown' in the 'New' button handler so the new task's input is always visible.",
    "Make 'New' tab-aware: intelligently switch to the Markdown tab only when needed (e.g., show the input area), preserving tab state otherwise.",
    "Fix the stale ItemsControl binding: ensure the task list's ItemsPanelRoot is properly initialized/rebound when switching tabs, so adding items via 'New' works reliably regardless of tab state."
  ]
}

## Stage 2 - Research

{
  "findings": "The 'New' button (OpenNewTaskDialogCommand) opens the new-task authoring form inside the Markdown tab, but never resets the TabControl's selected tab to Markdown. When the user is on the Attachments tab (index 2), clicking 'New' sets IsNewTaskDialogOpen=true but the form stays hidden because the UI still shows the Attachments tab. The TabControl in TaskDetailPanel.axaml has no SelectedIndex binding to the ViewModel — tab state is managed purely by the UI layer. Both OpenNewTaskDialog() (Authoring.cs:167-183) and CreateNewTaskAsync() (Authoring.cs:185-218) lack any tab-reset logic. The first click works because the default tab is Markdown (index 0), so the form appears visible.",
  "constraints": [
    "TabControl has no SelectedIndex binding to any ViewModel property — tab state is purely UI-managed",
    "Adding a ViewModel property for selected tab requires both ViewModel and XAML changes",
    "New-task form visibility (IsVisible={Binding IsNewTaskDialogOpen}) is inside the Markdown tab content — invisible when Attachments or Context tab is selected",
    "OpenNewTaskDialog() is a toggle — second invocation closes the dialog (Authoring.cs:169)",
    "CommunityToolkit.Mvvm [ObservableProperty] source generator convention: _camelCase fields",
    "Any fix must be testable at the ViewModel layer using existing headless unit test patterns",
    "Markdown tab is index 0, Context tab is index 1, Attachments tab is index 2 in the TabControl",
    "No code-behind exists for TaskDetailPanel.axaml — only XAML",
    "Existing NewTaskAuthoringTests.cs tests verify dialog open/close behavior but not tab state"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The 'New' button (bound to OpenNewTaskDialogCommand in QueuePanel.axaml line 26) sets IsNewTaskDialogOpen=true but never switches the TabControl back to the Markdown tab. The TabControl in TaskDetailPanel.axaml (line 101) has no SelectedIndex binding — its tab state is purely UI-managed. The new-task authoring form (lines 182-215: Grid with IsVisible=\"{Binding IsNewTaskDialogOpen}\") lives entirely inside the Markdown TabItem (line 103). When the user is on the Attachments tab (index 2), clicking 'New' opens the dialog but the TabControl stays on Attachments, so the form is invisible. The first click works only because the TabControl defaults to SelectedIndex=0 (Markdown). No ViewModel property for selected tab index exists anywhere in MainWindowViewModel.cs, and the code-behind (TaskDetailPanel.axaml.cs) is an empty stub with just InitializeComponent(). The TabControl also lacks an x:Name, so it cannot be programmatically addressed from code-behind without modification.",
  "excerpts": [
    "TaskDetailPanel.axaml:101-103 — TabControl has no SelectedIndex binding: `<TabControl Grid.Row=\"1\" Margin=\"16,4,16,16\"> <TabItem Header=\"Markdown\">`",
    "TaskDetailPanel.axaml:182-186 — New-task form is nested inside the Markdown TabItem only: `<Grid Grid.Row=\"1\" RowDefinitions=\"Auto,*,Auto\" IsVisible=\"{Binding IsNewTaskDialogOpen}\" Margin=\"4\">`",
    "TaskDetailPanel.axaml:229-230 — Attachments tab at index 2 has no new-task form: `<TabItem Header=\"Attachments\"> <Grid RowDefinitions=\"Auto,*\">`",
    "Authoring.cs:167-183 — OpenNewTaskDialog() only toggles IsNewTaskDialogOpen, never switches tabs: `private void OpenNewTaskDialog() { if (IsNewTaskDialogOpen) { IsNewTaskDialogOpen = false; ... return; } ... IsNewTaskDialogOpen = true; }`",
    "MainWindowViewModel.cs (full file) — No SelectedTabIndex or any tab-related [ObservableProperty] exists anywhere in the ViewModel",
    "TaskDetailPanel.axaml.cs:1-11 — Code-behind is an empty stub with only InitializeComponent(); no tab manipulation possible: `public partial class TaskDetailPanel : UserControl { public TaskDetailPanel() { InitializeComponent(); } }`",
    "QueuePanel.axaml:25-29 — The 'New' button binding: `<Button Grid.Column=\"2\" Command=\"{Binding OpenNewTaskDialogCommand}\" Padding=\"10,4\" MinHeight=\"28\" Content=\"New\"/>`"
  ],
  "repro": "1. Launch the app with a valid task repository. 2. Click a task in the queue to select it — the TaskDetailPanel shows the Markdown tab by default (index 0). 3. Click the 'New' button → the new-task authoring form (title + body textboxes, 'Create task'/'Cancel' buttons) appears inside the Markdown tab. This first click works. 4. Cancel the new-task dialog (click 'Cancel' or 'New' again). 5. Switch to the 'Attachments' tab in the TaskDetailPanel. 6. Click the 'New' button again. **Expected:** TabControl switches to the Markdown tab so the new-task form is visible. **Actual:** IsNewTaskDialogOpen becomes true but the TabControl stays on the Attachments tab. The new-task form is invisible to the user, making the feature appear broken."
}

## Stage 4 - Plan

{
  "plan": "Add a `SelectedTabIndex` observable property (default 0) to MainWindowViewModel. Bind `TabControl.SelectedIndex` to it in TaskDetailPanel.axaml. In `OpenNewTaskDialog()`, set `SelectedTabIndex = 0` when opening the dialog so the Markdown tab (containing the new-task form) is always visible. Add a headless unit test verifying the tab reset.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs",
    "src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml",
    "tests/VisualRelay.Tests/NewTaskAuthoringTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/NewTaskAuthoringTests.cs"],
  "rationale": "Added one new test method OpenNewTaskDialog_ResetsSelectedTabIndexToMarkdown (line 222). It asserts that when the user clicks 'New' while on a non-Markdown tab (e.g. Attachments at index 2, Context at index 1), the ViewModel's SelectedTabIndex property resets to 0 so the new-task authoring form — which lives inside the Markdown TabItem — becomes visible. This test will fail at compile time because SelectedTabIndex doesn't exist yet (TDD red phase). The test also verifies that closing the dialog doesn't spuriously change the tab, and that reopening from any non-zero tab consistently resets to 0."
}

## Stage 6 - Implement

{
  "summary": "## Changes\n\n### 1. `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` (line 178-179)\nAdded `[ObservableProperty] private int _selectedTabIndex;` — a new observable property that tracks which tab (Markdown=0, Context=1, Attachments=2) is selected. Defaults to 0 (Markdown) which is `int` default.\n\n### 2. `src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs` (line 182)\nIn `OpenNewTaskDialog()`, added `SelectedTabIndex = 0;` before setting `IsNewTaskDialogOpen = true`. This ensures the TabControl switches to the Markdown tab — where the new-task authoring form lives — whenever the New button is clicked, regardless of which tab the user was on.\n\n### 3. `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` (line 103)\nAdded `SelectedIndex=\"{Binding SelectedTabIndex}\"` to the `TabControl` element. This two-way binding connects the ViewModel property to the UI, so programmatic tab changes (like the New button reset) are reflected in the view, and user tab clicks update the ViewModel.\n\n### 4. `tests/VisualRelay.Tests/NewTaskAuthoringTests.cs` (lines 222-259)\nNew test `OpenNewTaskDialog_ResetsSelectedTabIndexToMarkdown` verifies: default tab is 0, user can switch to Attachments (index 2) or Context (index 1), clicking New resets to 0 so the authoring form is visible, and closing the dialog does not spuriously change the tab. Two `Assert.Equal` calls were changed to `Assert.True` to match xUnit's API (no message overload for `Assert.Equal<int>`).\n\n## Build & Test Results\n- App project: **0 warnings, 0 errors**\n- Test project: **0 warnings, 0 errors**\n- All 6 NewTaskAuthoringTests: **passed**"
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "No blockers or warnings to resolve. The Stage 7 review returned a clean pass with zero issues. All four manifest files have the correct implementation: SelectedTabIndex observable property, tab reset in OpenNewTaskDialog(), SelectedIndex binding on TabControl, and the accompanying unit test." }

## Stage 9 - Verify

{
  "summary": "Fixed the 'New' button so it always switches the TabControl to the Markdown tab when opening the new-task authoring form. Added `SelectedTabIndex` observable property to `MainWindowViewModel.cs` (line 178-179), bound `TabControl.SelectedIndex` to it in `TaskDetailPanel.axaml` (line 103), and set `SelectedTabIndex = 0` in `OpenNewTaskDialog()` in `Authoring.cs` (line 182). A new unit test (`OpenNewTaskDialog_ResetsSelectedTabIndexToMarkdown`) verifies that the tab resets to 0 (Markdown) regardless of which tab was selected. All 6 NewTaskAuthoringTests pass.",
  "commitMessages": [
    "fix: reset TabControl to Markdown tab when opening new-task dialog",
    "fix(new-button): always switch to Markdown tab so authoring form is visible",
    "fix: add SelectedTabIndex binding and reset it in OpenNewTaskDialog",
    "fix: ensure 'New' button shows authoring form by selecting the Markdown tab",
    "fix: prevent hidden new-task form by resetting selected tab to 0 on New"
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "Fix-verify confirmed. All 6 NewTaskAuthoringTests pass (including the new OpenNewTaskDialog_ResetsSelectedTabIndexToMarkdown), build produces 0 warnings and 0 errors. The implementation matches the plan: SelectedTabIndex observable property at MainWindowViewModel.cs:178-179, tab reset to 0 in OpenNewTaskDialog() at Authoring.cs:182, SelectedIndex binding at TaskDetailPanel.axaml:103, and the accompanying unit test at NewTaskAuthoringTests.cs:222-259. The 'New' button now always switches the TabControl to the Markdown tab so the authoring form is visible regardless of which tab the user was previously viewing."
}

## Stage 11 - Commit

Committed by Visual Relay.

