## Stage 1 - Ideate

{
  "summary": "Implement a sandboxed front-tier AI task-spec rewriter with worktree isolation, stopwatch, cancel/revert, and mutual exclusion with runs — the design is fixed; options trade off worktree strategy (git vs. temp dir) and runner invocation fidelity (full StageInvocation vs. slim interface).",
  "options": [
    "Option A: PlanningWorktree reuse — faithful spec-following",
    "Option B: Temp-directory isolation — lighter, git-free sandbox",
    "Option C: Lighter invocation — bypass StageInvocation, call runner directly"
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase is a .NET/Avalonia desktop app called Visual Relay that orchestrates multi-stage LLM task execution. Key architectural patterns confirmed:\n\n1. **TaskDetailPanel.axaml** (298 lines, at the 300-line guard) has a read-only toolbar at lines 124-139 containing `{Binding EditSelectedTaskCommand}` and `{Binding EditBlockedReason}` inside a `Border IsVisible=\"{Binding IsMarkdownReadOnly}\"` — the natural insertion point for the RewriteToolbar. Any new controls MUST be factored into a separate child control.\n\n2. **RelayTaskItem** exposes `CompletedStageCount == 0` as the canonical \"no run history\" test (line 21: `MetricsLine => CompletedStageCount == 0 ? \"No run history\" : ...`). Also gates on `!IsArchived`.\n\n3. **MainWindowViewModel** is split across 8+ partial files (Authoring.cs, Commands.cs, Execution.cs, LiveState.cs, Helpers.cs, Keys.cs, ObsidianBridge.cs, MainWindowViewModel.cs). Commands use `[RelayCommand(CanExecute = nameof(CanX))]` with `[NotifyCanExecuteChangedFor(...)]` on ObservableProperty attributes. Running state is tracked via `_runningTaskIds` (HashSet), `_runStartedAt` (Dictionary), `_runningTaskId` (single \"followed\").\n\n4. **Existing stopwatch**: `_elapsedTimer` (1s DispatcherTimer) in MainWindowViewModel.cs, started by `StartElapsedTimer()`, tick calls `UpdateRunningElapsedLabels()` which loops `_runningTaskIds` and sets `task.RunningElapsedLabel`. `ElapsedFormatter.Label(TimeSpan)` renders \"2m 25s\". Reuse this timer — do not add a second one.\n\n5. **PlanningWorktree** static class provides `CreateAsync(repoRoot, taskId, runId, ct, git)` → detached HEAD worktree under temp, `CopyConfigIntoWorktree(repoRoot, worktree)`, `CopyArtifactsBack(...)`, `RemoveAsync(...)`, `PruneLeftoversAsync(...)`. Uses `IGitInvoker` (defaults to `GitInvoker`).\n\n6. **SwivalSubagentRunner** implements `ISubagentRunner.RunAsync(StageInvocation, CancellationToken)` → `SubagentResult(RawText, Json?, IsValid, Error?)`. Every run is sandboxed via `nono run -p vr-guard --allow-cwd ... --rollback -- swival ...`. `StageInvocation` carries `(Stage, Tier, RunId, TargetRoot, TaskName, TaskInput, LedgerSoFar, Manifest, LogSources, TraceDirectory, ReportFile, MaxTurns, ...)`.\n\n7. **RelayStageDefinition** is a record with `(Number, Name, Tier, Kind, Files, Commands, SystemPrompt, OutputContract)`. Tier \"frontier\" is configured in `RelayConfigLoader.Defaults()` with profile \"frontier\".\n\n8. **Confirmation dialog**: `ShowConfirmationAsync` is a `Func<string,string,Task<bool>>?` delegate on MainWindowViewModel, wired in App.axaml.cs. When null (headless/tests) callers proceed without prompting.\n\n9. **Drain flow**: `DrainQueueAsync()` in Execution.cs builds `SwivalSubagentRunner`, `RelayQueueController`, calls `controller.RefreshAsync()`, `controller.ApplyOrder(...)`, `controller.DrainAsync()`. `DrainAsync` uses `var queue = Tasks.Where(task => !task.NeedsReview).ToList()` as its working queue.\n\n10. **CanRunSelected** (Helpers.cs:204) currently gates on `!IsBusy && !PauseRequested && SelectedTask is not null && !SelectedTask.IsArchived`. Must also gate on `!_rewritingTaskIds.Contains(SelectedTask.Id)`.\n\n11. **ControlApi** (Services/ControlApi.cs) has `ResolveCommand` switch and `IcommandNames` static array (line 98-102) listing all command names. `BuildCommandsMap()` in State.cs adds command enable states. New commands `rewrite-selected`, `cancel-rewrite`, `revert-rewrite` need to be registered in both.\n\n12. **RelayTaskWriter.SaveAsync(task, markdown)** (line 113-116) writes to `task.MarkdownPath`. `ReloadTaskListAsync(id)` refreshes the task list preserving selection.\n\n13. **Test patterns**: Unit tests use `[Fact]`, headless UI tests `[AvaloniaFact]`. Test doubles exist: `ScriptedSubagentRunner` (returns canned JSON per stage), `CapturingSubagentRunner` (records invocations), `ScriptedTestRunner`, `ScriptedOutcomeTaskRunner`, `TestRepository` (temp dir + helpers), `TestGit`, `ScratchRepo`. VM tests can invoke private methods via reflection (see LiveStateViewModelTests).\n\n14. **ObservableRelayEventSink** posts on `Dispatcher.UIThread.Post`, marshalling events to the UI thread.\n\n15. **File size guard**: `tools/guards/check-file-size.sh` enforces 300-line limit (configurable via `VISUAL_RELAY_FILE_LINE_LIMIT`). Every changed/added .cs and .axaml file must be ≤300 lines.",
  "constraints": [
    "Every .cs and .axaml source file must stay under 300 lines (enforced by tools/guards/check-file-size.sh; VISUAL_RELAY_FILE_LINE_LIMIT env var). TaskDetailPanel.axaml is already 298 lines — any edit requires extracting new controls into a child RewriteToolbar control.",
    "Headless UI tests must use [AvaloniaFact]/[AvaloniaTheory]; HeadlessUnitTestSession is banned (BannedApiAnalyzers).",
    "Conventional Commits subject enforced by commit-msg hook: fixed type set, ≤72-char subject, lowercase after prefix, no trailing period, no em dashes, body ≤3 hyphen bullets (≤20 words each).",
    "Rewrite must NOT gate on IsBusy — it must run concurrently with queue draining other tasks.",
    "Rewrite must NOT start a second DispatcherTimer — reuse the existing 1-second _elapsedTimer in MainWindowViewModel.cs, extending UpdateRunningElapsedLabels().",
    "Mutual exclusion: a task being rewritten must not be runnable (CanRunSelected/CanResumeSelected return false), must not be editable (CanEditSelectedTask blocked), and the drain queue must skip rewriting tasks.",
    "Worktree isolation via PlanningWorktree (git worktree + nono sandbox) is required — not a temp-directory-only approach.",
    "The rewrite prompt text must be embedded verbatim in RewriteGuidance.SystemPrompt as specified.",
    "Confirmation dialog uses the existing ShowConfirmationAsync delegate; when null (headless/tests) proceed without prompting.",
    "All core logic (RewriteGuidance, TaskRewriteRunner) must be testable with fake ISubagentRunner + IGitInvoker — no real process launch.",
    "Only the task's own folder (llm-tasks/<id>/) may be copied back from the worktree; stray writes outside that folder must be discarded.",
    "The pre-rewrite text snapshot is in-memory only (Dictionary<string,string>) and is dropped on: manual edit save, run start for that id, new rewrite start for same id, or app quit.",
    "Cancel must be a no-op-safe if the rewrite already finished/errored (guard the CancellationTokenSource lookup).",
    "ControlApi must register the three new commands (rewrite-selected, cancel-rewrite, revert-rewrite) in both ResolveCommand switch and IcommandNames array."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The codebase is in a healthy state for implementing the Rewrite with AI feature. The full test suite (1335 tests) passed clean on 2026-06-20. All architectural patterns needed — partial VM classes, CommunityToolkit [RelayCommand], PlanningWorktree, ISubagentRunner, SwivalSubagentRunner, ElapsedFormatter, ObservableRelayEventSink, ControlApi command registration — are confirmed present and working. The TaskDetailPanel.axaml sits at 298 lines (right at the 300-line guard), confirming the need to extract a child RewriteToolbar control. One flaky pre-existing test fails intermittently: RelayDriverGitCommitTests.RunTaskAsync_WhenAgentSelfCommitsMidRun_SquashesIntoOneSealedCommit — it is unrelated to the rewrite feature (it tests the git commit squash logic in the RelayDriver pipeline). Two failure modes observed: (A) precondition failure where AgentCommitLanded is false but outcome is Committed — the MidRunSelfCommittingRunner's `git commit` at stage 8 silently fails, yet the pipeline reports success; (B) the rev-list count is 2 instead of 1 — the squash fails to collapse the agent's self-commit into a single sealed commit, leaving a bare commit behind. This flaky test should not block the rewrite feature since it exercises a different code path (RelayDriver full pipeline vs. the new TaskRewriteRunner which runs standalone sandboxed rewrites outside the stage pipeline). The implementer should run targeted tests (e.g. 'dotnet test --filter Rewrite') to avoid the flaky test noise.",
  "excerpts": [
    "test-logs/20260621T000958_Manageds-Virtual-Machine_427.log:15: [FAIL] RunTaskAsync_WhenAgentSelfCommitsMidRun_SquashesIntoOneSealedCommit — Assert.Equal() Failure: Strings differ, Expected: \"1\", Actual: \"2\" at RelayDriverGitCommitTests.SelfCommitSquash.cs:42",
    "test-logs/20260621T000827_Manageds-Virtual-Machine_99557.log:15: [FAIL] RunTaskAsync_WhenAgentSelfCommitsMidRun_SquashesIntoOneSealedCommit — test precondition: agent's mid-run commit must have landed. outcome=Committed reason= at RelayDriverGitCommitTests.SelfCommitSquash.cs:38",
    "test-logs/20260621T001051_Manageds-Virtual-Machine_802.log:16: [PASS] RunTaskAsync_WhenAgentSelfCommitsMidRun_SquashesIntoOneSealedCommit (same test, same binary, passes 4 minutes after the failures — confirms flaky)",
    "test-logs/20260620T172855_Manageds-Virtual-Machine_68565.log:1381-1384: Full test suite: Total tests: 1335, Passed: 1322, Skipped: 13 — 0 failures across the entire suite",
    "tests/VisualRelay.Tests/CommitTestRunners.SelfCommit.cs:36: The MidRunSelfCommittingRunner does `Git(\"add -A\") == 0 && Git(\"commit -m agent-wip-bare\") == 0` at stage 8 — the git process can fail silently (exit code non-zero but not surfaced to the test precondition check beyond the bool flag)",
    ".relay/rewrite-task-with-ai/run.log: Stages 1 (Ideate) and 2 (Research) completed successfully; stage 3 (Diagnose) is currently running — the relay pipeline itself is functional and the task is progressing normally"
  ],
  "repro": "Run `./visual-relay test` repeatedly. The flaky test `RelayDriverGitCommitTests.RunTaskAsync_WhenAgentSelfCommitsMidRun_SquashesIntoOneSealedCommit` fails approximately 4 out of 5 runs on macOS. To isolate the rewrite feature: `dotnet test tests/VisualRelay.Tests/ --filter 'FullyQualifiedName~TaskRewriteRunner|FullyQualifiedName~RewriteGuidance|FullyQualifiedName~CanRewrite'` which avoids the flaky test entirely."
}

## Stage 4 - Plan

{
  "plan": "## Implementation plan: Rewrite with AI\n\n### Phase 0 — Tests first (red)\n\n**`tests/VisualRelay.Tests/RewriteGuidanceTests.cs`** (new, ≤100 lines):\n- `BuildInput_EmbedsSpecAndPath`: assert the output contains the verbatim current spec and the repo-relative path\n- `BuildInput_NeverExpandsScope`: assert the output does not mention files outside the task folder\n- `SystemPrompt_IsNotEmpty`: sanity check\n\n**`tests/VisualRelay.Tests/TaskRewriteRunnerTests.cs`** (new, ≤250 lines):\n- Custom fake `ISubagentRunner` that writes to the worktree spec file and also scribbles a stray file outside the task folder\n- `Success_CopiesOnlyTaskFolderBack`: verify the rewritten spec appears in the main tree, the stray file does NOT, and the worktree is removed\n- `Success_PreservesPreExistingDirtyFile`: create a dirty file in the main tree before rewrite; assert it's untouched after\n- `Cancellation_LeavesSpecByteIdentical`: cancel the token mid-run; assert the original spec is byte-for-byte unchanged; worktree removed\n- `Error_LeavesSpecUntouched`: throw from the fake runner; assert the original spec is untouched; worktree removed\n- Use `TestGit` for `IGitInvoker` and `TestRepository` for file scaffolding\n\n### Phase 1 — Core logic\n\n**`src/VisualRelay.Core/Execution/RewriteGuidance.cs`** (new, ≤50 lines):\n- `public static string SystemPrompt` = verbatim prompt from task spec\n- `public static string BuildInput(string currentSpec, string specRepoRelativePath)` — returns the per-run instruction framing current spec + exact path to overwrite + stay-in-folder constraint\n\n**`src/VisualRelay.Core/Execution/TaskRewriteRunner.cs`** (new, ≤120 lines):\n- `public static async Task<RewriteOutcome> RunAsync(string rootPath, RelayTaskItem task, RelayConfig config, ISubagentRunner runner, CancellationToken ct, IGitInvoker? git = null)`\n- Record type `RewriteOutcome(bool Changed, string? Error)`\n- Flow: create worktree → copy task folder into worktree → copy config → build `StageInvocation` with Tier=\"frontier\" and custom `RelayStageDefinition` → `runner.RunAsync(...)` → on success copy task folder back → read spec to detect change → finally remove worktree\n- Copy-back uses a helper `CopyTaskFolderIntoMainTree(rootPath, worktreePath, task)` that only copies `llm-tasks/<taskId>/`\n\n### Phase 2 — ViewModel rewrite state + commands\n\n**`src/VisualRelay.App/ViewModels/MainWindowViewModel.cs`** (edit, ~5 lines added):\n- Add backing fields after line 38: `_rewritingTaskIds` (HashSet), `_rewriteStartedAt` (Dictionary), `_rewriteCts` (Dictionary<string,CancellationTokenSource>), `_rewriteUndo` (Dictionary<string,string>)\n\n**`src/VisualRelay.App/ViewModels/MainWindowViewModel.Rewrite.cs`** (new partial, ≤180 lines):\n- `[RelayCommand(CanExecute=nameof(CanRewriteSelected))]` `RewriteSelectedTaskAsync()`: confirm → capture `_rewriteUndo[id]` → add to `_rewritingTaskIds` → set `_rewriteStartedAt[id]` → create `_rewriteCts[id]` → load config → build `SwivalSubagentRunner` → `Task.Run` the rewrite → marshal completion to UI thread → drop from dictionaries → reload task list\n- `CanRewriteSelected`: `SelectedTask is not null && !SelectedTask.IsArchived && CompletedStageCount == 0 && !IsEditingMarkdown && !IsNewTaskDialogOpen && id ∉ _runningTaskIds && id ∉ _rewritingTaskIds` (no IsBusy gate)\n- `CancelRewriteSelected`: `_rewriteCts[id]?.Cancel()` (no-op safe if already done)\n- `RevertRewriteSelected` `[RelayCommand(CanExecute=nameof(CanRevertSelected))]`: save `_rewriteUndo[id]` back → drop undo → reload\n- `CanRevertSelected`: `_rewriteUndo.ContainsKey(SelectedTask.Id) && !_rewritingTaskIds.Contains(SelectedTask.Id)`\n- Computed bind properties: `IsSelectedTaskRewriting`, `SelectedTaskRewriteElapsed`, `SelectedTaskHasRewriteUndo`\n- On every state transition: raise `NotifyCanExecuteChanged` and `OnPropertyChanged` for the bind targets\n\n**`src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs`** (edit, ~15 lines added):\n- In `UpdateRunningElapsedLabels()`: add loop over `_rewritingTaskIds` setting `task.RewriteElapsedLabel = ElapsedFormatter.Label(now - startedAt)` and set `SelectedTaskRewriteElapsed`\n- In `BeginRunningTask()` (line 100): after `_runningTaskIds.Add(task.Id)`, add `_rewriteUndo.Remove(task.Id)` (drop undo when run starts)\n\n**`src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs`** (edit, ~8 lines added):\n- In `CanEditSelectedTask()` (after line 32): add block: `if (SelectedTask is not null && _rewritingTaskIds.Contains(SelectedTask.Id)) { EditBlockedReason = \"Cannot edit a task while it's being rewritten.\"; return false; }`\n- In `SaveEditAsync()` (after line 63): add `_rewriteUndo.Remove(SelectedTask.Id)` (drop undo on manual edit save)\n\n**`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs`** (edit, ~3 lines added):\n- In `CanRunSelected()` (line 204): add `&& (SelectedTask is null || !_rewritingTaskIds.Contains(SelectedTask.Id))`\n\n**`src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs`** (edit, ~5 lines added):\n- In `DrainQueueAsync()` after `controller.RefreshAsync()` and `ApplyOrder`: remove any `controller.Tasks` whose Id is in `_rewritingTaskIds` before the drain proceeds (guard the drain queue)\n\n**`src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs`** (edit, ~3 lines added):\n- In `OnSelectedTaskChanged` (line 148): add `RewriteSelectedTaskCommand.NotifyCanExecuteChanged()`, `RevertRewriteSelectedCommand.NotifyCanExecuteChanged()`, and `OnPropertyChanged(...)` for rewrite bind targets\n\n**`src/VisualRelay.App/ViewModels/TaskRowViewModel.cs`** (edit, ~15 lines added):\n- Add `_rewriteElapsedLabel` field and `RewriteElapsedLabel` property (mirror `RunningElapsedLabel` pattern: raises `OnPropertyChanged(nameof(MetricsLine))`)\n- Update `MetricsLine` to show rewrite elapsed when rewriting, similar to running display\n- Update `MarkIdle()` to clear `_rewriteElapsedLabel`\n\n### Phase 3 — UI controls\n\n**`src/VisualRelay.App/Views/Controls/RewriteToolbar.axaml`** (new, ≤60 lines):\n- `UserControl x:DataType=\"vm:MainWindowViewModel\"`\n- Horizontal `StackPanel` with three visibility-toggled sections:\n  - Idle: `Button Command=\"{Binding RewriteSelectedTaskCommand}\" Content=\"Rewrite with AI\"` visible when `CanRewriteSelected`\n  - Rewriting: `ProgressBar IsIndeterminate=\"True\"` + `TextBlock Text=\"{Binding SelectedTaskRewriteElapsed}\"` + `Button Content=\"Cancel\" Command=\"{Binding CancelRewriteSelectedCommand}\"` visible when `IsSelectedTaskRewriting`\n  - Done: `Button Content=\"Revert\" Command=\"{Binding RevertRewriteSelectedCommand}\"` visible when `SelectedTaskHasRewriteUndo`\n\n**`src/VisualRelay.App/Views/Controls/RewriteToolbar.axaml.cs`** (new, ≤15 lines):\n- Standard code-behind: `public partial class RewriteToolbar : UserControl { public RewriteToolbar() { InitializeComponent(); } }`\n\n**`src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml`** (edit, ~5 lines changed):\n- In the read-only `Border` `StackPanel` (line 127): add `<controls:RewriteToolbar/>` as first child alongside the existing Edit `Button`\n- Add xmlns import if needed (`controls` already imported at line 4)\n- Ensure total stays ≤300 lines (extracting RewriteToolbar gives headroom; at 298 lines, adding ~2 lines for the control host and trimming a blank keeps it safe)\n\n### Phase 4 — Control API\n\n**`src/VisualRelay.App/Services/ControlApi.cs`** (edit, ~5 lines added):\n- In `ResolveCommand` switch: add cases `\"rewrite-selected\" => viewModel.RewriteSelectedTaskCommand`, `\"cancel-rewrite\"` → custom no-op-safe delegate, `\"revert-rewrite\" => viewModel.RevertRewriteSelectedCommand`\n\n**`src/VisualRelay.App/Services/ControlApi.State.cs`** (edit, ~3 lines added):\n- In `IcommandNames` array: add `\"rewrite-selected\"`, `\"cancel-rewrite\"`, `\"revert-rewrite\"`\n\n### Phase 5 — Integration verification\n\n- Run `./visual-relay check` — ensure build, tests, format, file-size guards all pass\n- Run `dotnet test --filter Rewrite` to verify new tests pass\n- Manual smoke: launch app, select fresh task, click Rewrite with AI, confirm dialog, watch stopwatch, cancel, verify revert\n\n### Deliverable coverage\n\nEvery \"Done when\" criterion maps to above:\n- **Tests pass red-first**: Phase 0 writes failing tests; Phase 1–2 make them green\n- **Live behavior (select → confirm → sandboxed rewrite → stopwatch → cancel → revert)**: Phases 2–3 wire the full flow\n- **Queue/other tasks keep working during rewrite**: `CanRewriteSelected` omits `IsBusy` gate; Phase 2 ensures rewrite runs on background task independent of drain\n- **Task not editable/runnable during rewrite**: Phase 2 edits to `CanEditSelectedTask`, `CanRunSelected`, and drain skip\n- **Cancel no-op-safe if already finished**: `CancelRewriteSelected` guards `_rewriteCts[id]?.Cancel()`\n- **Revert disappears on edit/run start/app quit**: Phase 2 undo lifecycle in `LiveState.cs`, `Authoring.cs`\n- **Only task's folder changed on disk**: Phase 1 `TaskRewriteRunner` copy-back only copies the task folder\n- **`./visual-relay check` green; all files <300 lines**: Phases 3–4 respect file-size guard; `RewriteToolbar` extraction keeps `TaskDetailPanel.axaml` ≤300\n- **Self-contained**: implementer needs only this plan file",
  "manifest": [
    "+tests/VisualRelay.Tests/RewriteGuidanceTests.cs",
    "+tests/VisualRelay.Tests/TaskRewriteRunnerTests.cs",
    "+src/VisualRelay.Core/Execution/RewriteGuidance.cs",
    "+src/VisualRelay.Core/Execution/TaskRewriteRunner.cs",
    "+src/VisualRelay.App/ViewModels/MainWindowViewModel.Rewrite.cs",
    "+src/VisualRelay.App/Views/Controls/RewriteToolbar.axaml",
    "+src/VisualRelay.App/Views/Controls/RewriteToolbar.axaml.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs",
    "src/VisualRelay.App/ViewModels/TaskRowViewModel.cs",
    "src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml",
    "src/VisualRelay.App/Services/ControlApi.cs",
    "src/VisualRelay.App/Services/ControlApi.State.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/RewriteGuidanceTests.cs",
    "tests/VisualRelay.Tests/TaskRewriteRunnerTests.cs"
  ],
  "rationale": "Both test files fail to compile because the target types (RewriteGuidance, TaskRewriteRunner) don't exist yet — exactly the expected TDD red state. RewriteGuidanceTests verifies SystemPrompt is non-empty, contains core rules (succinct, grounded, one decided direction, TDD-first, ./visual-relay check), BuildInput embeds spec+path and never mentions files outside the task folder. TaskRewriteRunnerTests uses a RewriteFakeRunner ISubagentRunner double that writes into the worktree manifest path and optionally scribbles stray files; it covers: success copies only the task folder back (stray discarded, worktree removed), preserves pre-existing dirty files, reports unchanged when spec identical, cancellation (pre-run and mid-run via PostWriteCancellationRunner) leaves the original spec byte-identical and cleans up the worktree, error from the runner leaves spec untouched and cleans up, and a sibling task folder is never copied. All 11 tests compile against existing types (RelayTaskItem, RelayConfig, StageInvocation, SubagentResult, etc.) and fail only on the two missing target types."
}

## Stage 6 - Implement

{
  "summary": "Implemented the Rewrite with AI feature across 16 files (10 new, 6 edited). All 1545 tests pass (0 failed, 13 skipped). All files ≤300 lines (TaskDetailPanel.axaml at 299). Core: RewriteGuidance (system prompt + BuildInput), TaskRewriteRunner (worktree-isolated sandboxed rewrite with copy-back only for the task folder). VM: MainWindowViewModel.Rewrite.cs partial with RewriteSelectedTask, CancelRewrite, RevertRewrite commands, backing fields in MainWindowViewModel.cs, elapsed labels in LiveState.cs, mutual exclusion in Helpers/Execution/Authoring, selection-change notifications in Commands.cs. UI: RewriteToolbar.axaml child control hosted in TaskDetailPanel.axaml. API: ControlApi + ControlApi.State.cs register rewrite-selected, cancel-rewrite, revert-rewrite. Tests: RewriteGuidanceTests (4), TaskRewriteRunnerTests (6), TaskRewriteRunnerCancellationTests (2), RewriteFakeRunners (2 fake ISubagentRunner impls)."
}

## Stage 7 - Review

{
  "verdict": "changes",
  "issues": [
    {
      "file": "src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs",
      "line": 194,
      "description": "UpdateRunningElapsedLabels() sets per-row RewriteElapsedLabel but never raises PropertyChanged for SelectedTaskRewriteElapsed, so the toolbar stopwatch text is static and does not tick."
    },
    {
      "file": "src/VisualRelay.App/ViewModels/MainWindowViewModel.Rewrite.cs",
      "line": 197,
      "description": "RaiseRewriteStateChanged() notifies only rewrite-specific commands. It omits EditSelectedTaskCommand, RunSelectedCommand and ResumeSelectedCommand, whose CanExecute depends on _rewritingTaskIds, leaving those buttons stale when a rewrite starts or ends."
    },
    {
      "file": "src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs",
      "line": 69,
      "description": "SaveEditAsync() removes _rewriteUndo[id] but does not call RaiseRewriteStateChanged(), so the Revert button remains visible/enabled after a manual edit even though undo is gone."
    },
    {
      "file": "src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs",
      "line": 112,
      "description": "BeginRunningTask() removes _rewriteUndo[id] but does not call RaiseRewriteStateChanged(), so the Revert button can stay visible when a run starts after a completed rewrite."
    },
    {
      "file": "src/VisualRelay.Core/Execution/TaskRewriteRunner.cs",
      "line": 104,
      "description": "The success gate only checks !result.IsValid when result.Error is non-null. If the runner returns IsValid=false with a null Error, the code falls through and copies the worktree back, treating a failed rewrite as success."
    }
  ]
}

## Stage 8 - Fix

{
  "summary": "Resolved all 5 review issues with minimal, targeted edits. Fix 1 (LiveState.cs): added OnPropertyChanged for SelectedTaskRewriteElapsed after the rewrite elapsed loop so the toolbar stopwatch ticks every second. Fix 2 (Rewrite.cs): added RunSelectedCommand, ResumeSelectedCommand, and EditSelectedTaskCommand NotifyCanExecuteChanged to RaiseRewriteStateChanged so run/resume/edit buttons stay fresh when rewrite state transitions. Fix 3 (Authoring.cs): added RaiseRewriteStateChanged() after _rewriteUndo.Remove in SaveEditAsync so the Revert button disappears after a manual edit. Fix 4 (LiveState.cs): added RaiseRewriteStateChanged() after _rewriteUndo.Remove in BeginRunningTask so the Revert button disappears when a run starts. Fix 5 (TaskRewriteRunner.cs): changed the success gate from `!result.IsValid && result.Error is not null` to just `!result.IsValid` — the old code fell through to copy-back when IsValid=false with null Error, treating a failed rewrite as success. Full suite: 1532 passed, 0 failed, 13 skipped. All changed files ≤271 lines."
}

## Stage 9 - Verify

{
  "summary": "Fully implemented Rewrite with AI: a sandboxed frontier-model task-spec rewriter using git worktree isolation (PlanningWorktree), nono/vr-guard sandbox, stopwatch, cancel, revert, and mutual exclusion with runs/edits. Core: RewriteGuidance + TaskRewriteRunner. VM: MainWindowViewModel.Rewrite.cs partial with 3 commands, reuse of existing 1-second elapsed timer, undo lifecycle (dropped on edit/run start/app quit). UI: RewriteToolbar control (3-state: Rewrite/Spinner+Cancel/Revert) hosted in TaskDetailPanel.axaml (299 lines). Control API registration. 31/31 rewrite-specific tests pass; the full suite (1532 tests, 0 failed, 13 skipped) passes. All files under 300-line guard.",
  "commitMessages": [
    "feat(core): rewrite-with-ai sandboxed frontier task-spec rewriter",
    "feat(app): worktree-isolated ai rewrite with cancel, revert and stopwatch",
    "feat(vm): RewriteSelectedTask command gated on CompletedStageCount==0, non-blocking",
    "feat(app): sandboxed rewrite-toolbar control with live elapsed and revert"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

