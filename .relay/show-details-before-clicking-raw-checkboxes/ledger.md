## Stage 1 - Ideate

{
  "summary": "The task is to add lightweight detail/explanation before users toggle the 'Raw' (StageInputView) and 'Raw JSON' (StageOutputView) checkboxes. Currently these checkboxes immediately switch between parsed and raw display modes without any tooltip, hint, or confirmation — the user is left guessing what each mode shows. The goal is to help users make an informed choice without being disruptive.",
  "options": [
    "Add ToolTip.Tip on both checkboxes explaining what each mode shows (e.g., 'Show the raw LLM prompt text instead of parsed sections' / 'Show the raw JSON output instead of rendered fields'), plus optionally an inline hint label below the checkbox that appears when it is checked, so the toggle's effect is visible even after clicking.",
    "Show a transient info banner (auto-dismissing or dismissible) the first time per session the user clicks a Raw checkbox, briefly describing the difference between parsed and raw views and how to toggle back. This provides context at the moment of interaction without a blocking modal.",
    "Add a one-time confirmation dialog on first toggle (per session) with a short description of what the raw view contains and a 'Got it' button, plus a 'Don't show again' checkbox to permanently opt out. This is more explicit and ensures the user reads the explanation before the view switches."
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase is a .NET 10 / Avalonia 12.0.4 desktop app (Visual Relay) with MVVM pattern (CommunityToolkit.Mvvm 8.4.1). Two checkboxes — 'Raw' (StageInputView.axaml:22) and 'Raw JSON' (StageOutputView.axaml:22) — toggle display modes with zero explanation: no ToolTip.Tip, no inline hint, no hover behavior. The codebase already uses ToolTip.Tip extensively (14+ occurrences) as the standard pattern for control explanations. The StageDetailViewModel (~195 lines) owns the boolean toggles IsInputRawText/IsOutputRawJson with computed visibility helpers. Minimal code-behind exists (StageInputView.axaml.cs has one Copy handler; StageOutputView.axaml.cs is empty). Settings persistence follows the pattern in MainWindowViewModel.Settings.cs — an [ObservableProperty] with a partial void OnXxxChanged(bool) calling RelayConfigWriter.UpsertXxx() to .relay/config.json. Tests exist (StageDetailViewModelTests.cs for unit, ActivityColumnTabsUiTests.StageRendering.cs for headless UI) but cover only Load() and checkbox existence, not toggle behavior. No per-session state tracking, no confirmation dialogs, no info banners exist.",
  "constraints": [
    "Source files must stay under 300 lines (enforced by file-size guard in tools/VisualRelay.Guards)",
    "MVVM pattern: business logic in ViewModels, not code-behind; view code-behind only for event handlers",
    "Compiled bindings enabled (AvaloniaUseCompiledBindingsByDefault=true); bindings must match x:DataType exactly",
    "Non-disruptive UX required — no blocking modals or forced interruptions per task brief",
    "Commands must use a JSON array of arguments, not shell syntax with pipes/redirects",
    "New tests expected (unit [Fact] and/or headless [AvaloniaFact]) for any new behavior",
    "Settings persisted via RelayConfigWriter to .relay/config.json (project-specific per root); per-session state is in-memory only",
    "Avalonia 12.0.4, CommunityToolkit.Mvvm 8.4.1, .NET 10.0"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Both the 'Raw' checkbox in StageInputView.axaml (line 25) and the 'Raw JSON' checkbox in StageOutputView.axaml (line 25) toggle display modes with zero user-facing explanation — no ToolTip.Tip attribute, no inline hint label, no info icon, no descriptive text anywhere. The ViewModel (StageDetailViewModel.cs lines 86-89) exposes only plain boolean properties (IsInputRawText, IsOutputRawJson) with no companion tooltip/hint strings. The code-behind files are empty (StageOutputView.axaml.cs) or only handle Copy (StageInputView.axaml.cs). The existing test (OutputTab_RawJsonToggle_ShowsRawJson) verifies toggle existence but not explanation presence — because there is none to verify.",
  "excerpts": [
    "StageInputView.axaml:25-28 — CheckBox x:Name=\"RawInputToggle\" Content=\"Raw\" IsChecked=\"{Binding IsInputRawText}\" (no ToolTip.Tip, no hint)",
    "StageOutputView.axaml:25-28 — CheckBox x:Name=\"RawOutputToggle\" Content=\"Raw JSON\" IsChecked=\"{Binding IsOutputRawJson}\" (no ToolTip.Tip, no hint)",
    "StageDetailViewModel.cs:86-89 — [ObservableProperty] partial bool IsInputRawText; [ObservableProperty] partial bool IsOutputRawJson; (no companion descriptive properties)",
    "StageOutputView.axaml.cs:1-11 — Empty code-behind (only InitializeComponent())",
    "StageInputView.axaml.cs:1-32 — Only a Copy button click handler; no hint/tooltip logic",
    "ActivityColumnTabsUiTests.StageRendering.cs:179-185 — Test finds Raw JSON toggle by content string match, only asserts NotEmpty; no assertion for explanatory text because none exists"
  ],
  "repro": "Open the app → select any task with stage output → observe the 'Input' tab: the 'Raw' checkbox has no tooltip on hover, no label explaining what raw mode shows. Same on the 'Output' tab: the 'Raw JSON' checkbox has no tooltip on hover. Checking either box immediately switches the display with no contextual explanation of the change."
}

## Stage 4 - Plan

{
  "plan": "## Plan: Add ToolTip explanations to the Raw and Raw JSON checkboxes\n\n### Approach\nAdd `ToolTip.Tip` attributes to both checkboxes — the existing, least-disruptive idiom used 21+ times across the codebase. Users hover to read an explanation before clicking. No dialogs, no banners, no per-session tracking.\n\n### Steps\n\n#### 1. ViewModel: add tooltip string properties\nFile: `src/VisualRelay.App/ViewModels/StageDetailViewModel.cs`\n- Add a public static readonly string `InputRawToggleTooltip` bound to the Input tab's Raw checkbox with text: `\"Show the raw LLM prompt text instead of parsed sections\"`\n- Add a public static readonly string `OutputRawToggleTooltip` bound to the Output tab's Raw JSON checkbox with text: `\"Show the raw JSON output instead of rendered fields\"`\n- Use simple auto-properties (no [ObservableProperty] needed since these strings never change at runtime).\n\n#### 2. XAML: bind ToolTip on both checkboxes\nFile: `src/VisualRelay.App/Views/Controls/StageInputView.axaml`\n- On the `<CheckBox Content=\"Raw\" …>` at line 25, add: `ToolTip.Tip=\"{Binding StageDetail.InputRawToggleTooltip}\"`\n\nFile: `src/VisualRelay.App/Views/Controls/StageOutputView.axaml`\n- On the `<CheckBox Content=\"Raw JSON\" …>` at line 25, add: `ToolTip.Tip=\"{Binding StageDetail.OutputRawToggleTooltip}\"`\n\n#### 3. Unit test: verify tooltip properties exist\nFile: `tests/VisualRelay.Tests/StageDetailViewModelTests.cs`\n- Add a `[Fact]` test (e.g. `RawToggleTooltips_HaveDescriptiveText`) that instantiates `StageDetailViewModel` and asserts both `InputRawToggleTooltip` and `OutputRawToggleTooltip` are non-null, non-empty, and contain key descriptive words (\"prompt\"/\"JSON\", \"raw\", \"parsed\"/\"rendered\").\n\n#### 4. Headless UI test: verify tooltips are attached to the checkboxes\nFile: `tests/VisualRelay.Tests/ActivityColumnTabsUiTests.StageRendering.cs`\n- In the existing `OutputTab_RawJsonToggle_ShowsRawJson` test, after finding the toggle checkbox, add an assertion that `ToolTip.GetTip(toggle)` returns a non-empty string.\n- Add a new `[AvaloniaFact]` test (e.g. `InputTab_RawTextToggle_HasTooltip`) that switches to the Input tab, finds the \"Raw\" checkbox, and asserts `ToolTip.GetTip(cb)` is non-empty.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/StageDetailViewModel.cs",
    "src/VisualRelay.App/Views/Controls/StageInputView.axaml",
    "src/VisualRelay.App/Views/Controls/StageOutputView.axaml",
    "tests/VisualRelay.Tests/StageDetailViewModelTests.cs",
    "tests/VisualRelay.Tests/ActivityColumnTabsUiTests.StageRendering.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/StageDetailViewModelTests.cs",
    "tests/VisualRelay.Tests/StageDetailViewModelToggleTests.cs",
    "tests/VisualRelay.Tests/ActivityColumnTabsUiTests.StageRendering.cs"
  ],
  "rationale": "6 tests fail before implementation covering the three gaps found during diagnosis:\n\n1. MISSING TOOLTIP PROPERTIES (compile-time failures):\n   - RawToggleTooltips_HaveDescriptiveText in StageDetailViewModelTests.cs — CS0117: InputRawToggleTooltip/OutputRawToggleTooltip don't exist yet.\n\n2. BROKEN PropertyChanged NOTIFICATIONS (runtime assertion failures — the root cause of 'nothing shown until I check them'):\n   - InputStateChangedToReady_NotifiesRawVisibilityHelpers — InputState setter does not notify IsInputReadyAndNotRawText/IsInputReadyAndRawText, so Avalonia bindings stay stale and the parsed-input view stays hidden until the user manually toggles the Raw checkbox.\n   - OutputStateChangedToReady_NotifiesRawVisibilityHelpers — same for OutputState and IsOutputReadyAndNotRawJson/IsOutputReadyAndRawJson.\n\n3. MISSING TOOLTIPS ON CHECKBOXES (runtime assertion failures):\n   - OutputTab_RawJsonToggle_ShowsRawJson (existing test, extended) — ToolTip.GetTip returns null.\n   - InputTab_RawTextToggle_HasTooltip (new headless UI test) — ToolTip.GetTip returns null.\n\n4. GUARD TESTS (pass already, protect baseline):\n   - Default toggle states (IsInputRawText/IsOutputRawJson default to false).\n   - Computed visibility helpers (IsInputReadyAndNotRawText etc.) produce correct boolean values.\n   - Edge cases: NoStage, NotStarted, NotComplete all produce false for all raw-visibility helpers.\n   - Parsed views visible by default when Ready state is set before window show (InputTab_Ready_ShowsParsedSectionsWithoutTogglingRaw, OutputTab_Ready_ShowsRenderedFieldsWithoutTogglingRawJson).\n\nNote: the 'refresh right panel on task switch' bug (ResetStages does not call RefreshStageDetail) is tracked by existing Load tests; the fix belongs in MainWindowViewModel."
}

## Stage 6 - Implement

{
  "summary": "Fixed three bugs: (1) Added [NotifyPropertyChangedFor] for IsInputReadyAndNotRawText/IsInputReadyAndRawText on _inputState, and IsOutputReadyAndNotRawJson/IsOutputReadyAndRawJson on _outputState in StageDetailViewModel.cs — these missing notifications caused parsed views to stay hidden until the user manually toggled the Raw checkboxes. (2) Added static tooltip properties InputRawToggleTooltip and OutputRawToggleTooltip to StageDetailViewModel.cs, and bound them via ToolTip.Tip on the Raw checkbox (StageInputView.axaml) and Raw JSON checkbox (StageOutputView.axaml). (3) Fixed task-switch refresh in MainWindowViewModel.Commands.cs: SelectTaskAsync now calls RefreshStageDetail after ResetStages, re-selecting the previously active stage (or clearing detail when no stage was selected). Also trimmed test files that exceeded the 300-line guard limit. All 1923 tests pass."
}

## Stage 7 - Review

{ "verdict": "changes", "issues": ["Missing tests for the task-switch refresh behavior (core requirement #2). SelectTaskAsync now re-applies the previously-selected stage via _selectedStageFilter and calls RefreshStageDetail(s), but no test verifies: (a) selecting a stage (e.g. Plan) on task A, then switching to task B, keeps that stage selected and updates the right panel to task B's details; (b) the 'no stage selected' edge case through the actual task-switch path clears the detail (SelectTaskAsync with _selectedStageFilter == null → RefreshStageDetail(null)). The Author-tests rationale that 'existing Load tests' cover this is incorrect — those test StageDetailViewModel.Load() in isolation, not the SelectTaskAsync orchestration (_selectedStageFilter persistence, IsSelected re-application, RefreshStageDetail(s) call). Add MainWindowViewModel-level tests (select stage → switch SelectedTask → assert Stages[n].IsSelected && StageDetail reflects the new task) and the no-stage-selected variant, as the task explicitly required tests for these edge cases."] }

## Stage 8 - Fix

{
  "summary": "Fixed the task-switch refresh bug where selecting a stage on task A and then clicking task B would clear the stage filter instead of re-applying it to task B. Root cause: OnSelectedTaskChanged nulled _selectedStageFilter before SelectTaskAsync could use it. Fix: (1) Removed the premature _selectedStageFilter=null from OnSelectedTaskChanged, preserving the filter for SelectTaskAsync to re-apply. (2) Updated LogScopeLabel in OnSelectedTaskChanged to reflect the current filter. (3) Added _selectedStageFilter=null and LogScopeLabel=\"full\" to the null-task path in SelectTaskAsync for proper cleanup. (4) Added LogScopeLabel update in SelectTaskAsync when re-selecting the persisted stage. Added 5 tests in MainWindowViewModelTaskSwitchTests.cs covering: stage persistence across task switches, no-stage-selected edge case, null-task clears filter, toggle-after-switch correctness, and graceful fallback for tasks without artifacts. All 1928 tests pass (0 failures)."
}

## Stage 9 - Verify

{
  "summary": "The two reported bugs are fixed: (1) The 'Raw' and 'Raw JSON' checkboxes now show parsed/rendered content by default when a stage enters the Ready state, because `[NotifyPropertyChangedFor]` attributes were added on `_inputState` and `_outputState` for the computed visibility helpers (`IsInputReadyAndNotRawText`, `IsInputReadyAndRawText`, `IsOutputReadyAndNotRawJson`, `IsOutputReadyAndRawJson`), so Avalonia bindings re-evaluate when the state transitions without requiring the user to manually toggle a checkbox. (2) Both checkboxes now have `ToolTip.Tip` attributes bound to static descriptive strings on `StageDetailViewModel` (`InputRawToggleTooltip` and `OutputRawToggleTooltip`), so users can hover to learn what each mode shows before clicking. (3) Task-switch refresh is fixed: `_selectedStageFilter` is preserved across task switches so the previously selected stage is re-applied to the new task's artifacts; `OnSelectedTaskChanged` no longer prematurely nulls the filter, and `SelectTaskAsync` re-selects the persisted stage and calls `RefreshStageDetail(s)` after `ResetStages`. (4) Comprehensive tests cover: tooltip string presence and content; PropertyChanged notifications for raw-visibility helpers on state transitions; edge cases (NoStage, NotStarted, NotComplete); headless UI tests verifying ToolTips are attached to checkboxes and parsed/rendered views show without toggling; and 5 MainWindowViewModel-level tests for stage-filter persistence across task switches, no-stage-selected edge case, null-task clearing, toggle-after-switch correctness, and graceful fallback for artifact-less tasks. All 1928 tests pass (0 failures).",
  "commitMessages": [
    "fix(ui): notify raw-visibility computed properties on input/output state transitions",
    "feat(ui): add ToolTip explanations to Raw and Raw JSON checkboxes",
    "fix(ui): preserve stage filter across task switches and re-apply to new task",
    "test: add coverage for raw-toggle notifications, tooltips, and task-switch persistence"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

