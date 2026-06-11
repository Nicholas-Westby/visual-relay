## Stage 1 - Ideate

{
  "summary": "Create a new `tools/VisualRelay.DrainQueue` console project that connects headless/CI users to the existing two-phase `RelayQueueController.DrainAsync` (parallel planning, serial execution). The core challenge is that the two-phase constructor is currently only wired in `MainWindowViewModel.Execution.cs` (Avalonia GUI). Three architectural approaches differ in where the wiring/shared logic lives.",
  "options": [
    "Option A — Self-contained CLI mirroring GUI wiring (closest to the suggested approach). New `VisualRelay.DrainQueue` project directly instantiates `RelayQueueController` with the two-phase constructor, builds per-task `SwivalSubagentRunner` + `ShellTestRunner` for planning and an `IRelayTaskRunner` (RelayDriver, CreateGitCommit:true, Resume:true) for execution, rewrites the `Tasks` collection after `RefreshAsync` to enforce subset/order, and maps outcomes to exit codes. Pros: touches no existing projects, self-contained, fast to implement. Cons: duplicates the GUI's wiring logic — a subtle divergence between GUI and CLI drain behavior becomes possible if both are not kept in sync. Unit-testable units (argument parser, outcome→exit-code mapper, summary formatter) live in the same project. Recommended if minimizing cross-project churn is highest priority.",
    "Option B — Extract shared drain orchestration into a library. Pull the `DrainQueueAsync` orchestration logic from `MainWindowViewModel.Execution.cs` into a shared `VisualRelay.QueueDrain` (or `VisualRelay.Core`) class library. The GUI calls the library; the new CLI calls the same library. The CLI project stays extremely thin (argument parse → library call → exit). Pros: single source of truth for drain semantics, no divergence risk, library is independently unit-testable. Cons: touches the GUI project (extraction refactor), more up-front refactoring, may require introducing seams into `RelayQueueController` if none exist for task-subset rewriting. Recommended if long-term maintainability and consistency between GUI/CLI paths is the top concern.",
    "Option C — Add a thin seam to `RelayQueueController` itself. Instead of wiring externally, add an extension method or a new method on `RelayQueueController` (e.g., `DrainSubsetAsync(IReadOnlyList<RelayTask>, ...)`) that accepts a pre-filtered/ordered task list and an `ILogger` for prefixed output, handling both phases internally. The CLI project is a few-line entry point; the GUI can optionally switch to the same seam later. Pros: maximum reuse — the controller already owns the drain loop — and the CLI has near-zero logic. Cons: modifies the controller class (riskier if it is heavily tested/used), blurs the controller's responsibility boundary, and the output-prefixing concern (per-task console attribution) may not fit naturally in the controller layer. Recommended if the team prefers to put behavior closest to the data it operates on."
  ]
}

## Stage 2 - Research

{
  "findings": "## Codebase Investigation Findings for headless-queue-drain-with-parallel-planning\n\n### Project Structure\n- **src/VisualRelay.Domain** — Domain models: `RelayConfig`, `RelayTaskItem`, `RelayTaskOutcome`, `RelayTaskOutcomeStatus`, `RelayEvent`, `RelayQueueState`\n- **src/VisualRelay.Core** — Core logic: `RelayQueueController` (Queue/), `RelayDriver` (Execution/), `PlanPhaseRunner` (Execution/), `SwivalSubagentRunner` (Execution/), `ShellTestRunner` (Execution/), `RelayConfigLoader` (Configuration/), logging sinks (Logging/)\n- **src/VisualRelay.App** — Avalonia GUI: `MainWindowViewModel.Execution.cs`, `GuiTaskRunner`, `ObservableRelayEventSink` (depends on `Dispatcher.UIThread`)\n- **tools/VisualRelay.RunTask** — Existing single-task CLI tool (simple Program.cs, fully serial)\n- **tests/VisualRelay.Tests** — xUnit v3 test project with extensive test doubles (TestRepository, ScriptedSubagentRunner, RecordingTaskRunner, ScriptedTestRunner, etc.)\n\n### RelayQueueController (src/VisualRelay.Core/Queue/RelayQueueController.cs)\n- **Two constructors**:\n  1. Legacy: `(string rootPath, IRelayTaskRunner runner)` — serial-only drain\n  2. **Two-phase**: `(string rootPath, IRelayTaskRunner runner, Func<string, ISubagentRunner>? planSubagentRunnerFactory, ITestRunner? planTestRunner, Func<string, IRelayEventSink>? planEventSinkFactory, DrainLifecycleCallbacks? lifecycle)` — enables parallel planning + serial execution\n- **DrainAsync** (line 95): \n  - Snapshots `Tasks.Where(task => !task.NeedsReview)` at start (line 109)\n  - **Phase 1** (lines 112-195): If two-phase constructor used, runs planning stages 1-4 via `PlanPhaseRunner.RunPlanPhaseAsync` with concurrency bounded by `config.MaxPlanConcurrency`. Planned-but-not-flagged tasks stay in the queue for Phase 2. Flagged tasks get NEEDS-REVIEW markers and are removed from queue.\n  - **Phase 2** (lines 198-243): Serial execution using `_runner.RunTaskAsync` (the `IRelayTaskRunner` passed to the constructor). Circuit breaker can halt the drain.\n- **Tasks** is `ObservableCollection<RelayTaskItem>` — can be cleared/reordered after `RefreshAsync()` before `DrainAsync()` to enforce subset/order (lines 53, 56-66, 77-93).\n- **RefreshAsync** populates Tasks from `RelayTaskRepository.ListPendingAsync()` which excludes NEEDS-REVIEW tasks and orders by Id (case-insensitive).\n\n### GUI Wiring (MainWindowViewModel.Execution.cs, lines 57-124)\n- `DrainQueueAsync` builds:\n  - **planSinkFactory** (line 76-77): `_ => new ObservableRelayEventSink(HandleRelayEvent)` — each task gets same UI sink\n  - **executeSink** (line 79): `new ObservableRelayEventSink(HandleRelayEvent)` — shared UI sink for execution phase\n  - **planSubagentFactory** (lines 82-83): `taskId => new SwivalSubagentRunner(config, eventSink: new ObservableRelayEventSink(HandleRelayEvent))`\n  - **planTestRunner** (line 84): `new ShellTestRunner(...)`\n  - **execution runner**: `GuiTaskRunner(RootPath, config, executeSink, executeTestRunner)`\n- `GuiTaskRunner` (in VisualRelay.App, can't be reused by console tool):\n  - Creates per-task `FileRelayEventSink` to `.relay/<taskId>/run.log`\n  - Wraps in `CompositeRelayEventSink(sharedSink, fileSink)`\n  - Creates new `SwivalSubagentRunner(config, eventSink: sink)` per call\n  - Creates `RelayDriver` with `CreateGitCommit: true, Resume: true`\n\n### Key Interfaces (src/VisualRelay.Core/Execution/Interfaces.cs)\n- `IRelayTaskRunner`: `Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken)`\n- `ISubagentRunner`: `Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken)`\n- `ITestRunner`: `Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken)`\n- `IRelayEventSink`: `Task PublishAsync(RelayEvent relayEvent, CancellationToken)`\n\n### RelayDriverOptions (src/VisualRelay.Core/Execution/RelayDriverOptions.cs)\n- `CreateGitCommit` (bool) — true for Phase 2, false for planning\n- `Resume` (bool) — true for Phase 2 to pick up Phase 1's completed stages 1-4\n- `LastStageToRun` (int?) — set to 4 for planning phase to stop after stage 4\n\n### PlanPhaseRunner (src/VisualRelay.Core/Execution/PlanPhaseRunner.cs)\n- `RunPlanPhaseAsync(mainRootPath, tasks, config, testRunner, ct, eventSinkFactory)`\n- Runs tasks concurrently bounded by `SemaphoreSlim(config.MaxPlanConcurrency)`\n- Creates per-task worktrees, runs stages 1-4 with `RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 4)`\n- Each task gets its own event sink via `eventSinkFactory(taskId)` (or NullRelayEventSink)\n- Also creates a per-task `FileRelayEventSink` writing to worktree's `.relay/<taskId>/run.log`\n- After planning, artifacts are copied back via `PlanningWorktree.CopyArtifactsBack`\n\n### Console Output Attribution\n- The `RelayEvent.DisplayLine` and `RelayEvent.DetailLine` (in Domain) already have `TaskId` field\n- A custom `IRelayEventSink` for console output can format `[taskId] <event>` prefixed lines\n- The `PlanPhaseRunner` already supports per-task event sinks via `eventSinkFactory(taskId)`\n\n### Existing Tests\n- `RelayQueueControllerTwoPhaseTests` (tests/VisualRelay.Tests/) — 5 facts covering:\n  - Happy path: parallel plan then serial execute\n  - Flagged plan task excluded from Phase 2\n  - Already-planned tasks skip Phase 1\n  - All planning tasks flagged → Phase 2 skipped entirely\n  - Lifecycle callbacks invoked correctly\n- Extensive test doubles: `TestRepository`, `ScriptedSubagentRunner`, `RecordingTaskRunner`, `ScriptedTestRunner`, `FlagAtStageSubagentRunner`, `PlanPhaseTestHelpers`, etc.\n\n### Solution File (VisualRelay.slnx)\n- No existing `VisualRelay.DrainQueue` project; needs to be added to `/tools/` folder\n- Test project references `VisualRelay.Domain`, `VisualRelay.Core`, and `VisualRelay.App`\n\n### ActiveTaskLock (src/VisualRelay.Core/Execution/ActiveTaskLock.cs)\n- Prevents concurrent task runs by PID check on `ACTIVE/info.json`\n- During Phase 1, planning runs in worktrees (different rootPath), so no conflict\n- During Phase 2, tasks run serially, so no conflict",
  "constraints": [
    "GuiTaskRunner is in src/VisualRelay.App (Avalonia-dependent); the new console project cannot reference VisualRelay.App. A console-compatible IRelayTaskRunner must be created in the new project, mirroring GuiTaskRunner's logic (RelayDriver, CreateGitCommit:true, Resume:true, per-task run.log via FileRelayEventSink).",
    "ObservableRelayEventSink uses Avalonia Dispatcher.UIThread, which is not available in a console app. A ConsoleRelayEventSink (similar to the one in tools/VisualRelay.RunTask/Program.cs) must be used, but with task-id prefixing for attributable output during parallel planning.",
    "The target repo's .relay/config.json governs MaxPlanConcurrency; the tool must load config via RelayConfigLoader.LoadAsync from the target root — nothing about the target may be assumed .NET or otherwise.",
    "Task subset/order enforcement: After controller.RefreshAsync(), the Tasks (ObservableCollection<RelayTaskItem>) must be cleared and repopulated with only the requested task IDs in the given order. Unknown task IDs (not in the pending set) must be detected, printed, and cause exit 2 without touching anything.",
    "Exit code 0 = success (all committed or empty queue); exit 2 = any task flagged/failed or drain halted or usage error (unknown task IDs).",
    "The new project must follow the same SDK/package pattern as tools/VisualRelay.RunTask (net10.0, ImplicitUsings, Nullable enable, TreatWarningsAsErrors).",
    "Unit tests for argument parsing, unknown-ID rejection, outcome-set-to-exit-code mapping, and ordering must be added (likely in the existing tests/VisualRelay.Tests project or a new test project).",
    "Empty queue must exit 0 with a clear 'nothing pending' message (no crash).",
    "Per-task run.log must be written to .relay/<taskId>/run.log (same as GuiTaskRunner does), even during parallel planning phase.",
    "The console tool must print one outcome line per task (matching RunTask's 'Status: taskId sha-or-reason' format) plus a final summary line (committed/flagged/failed/planned counts).",
    "All event lines printed to console must be prefixed with the task ID for attributable output while planning interleaves.",
    "The new project must be added to VisualRelay.slnx under the /tools/ folder.",
    "The existing RelayQueueController.DrainAsync behavior must not be modified; if a seam is missing, prefer adding a seam over duplicating drain logic."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The two-phase RelayQueueController.DrainAsync (parallel planning Phase 1 + serial execution Phase 2) is fully implemented in RelayQueueController.cs:95-249 but wired only by MainWindowViewModel.DrainQueueAsync() in the Avalonia GUI (MainWindowViewModel.Execution.cs:88-94). The existing console tool tools/VisualRelay.RunTask/Program.cs bypasses RelayQueueController entirely, instantiating RelayDriver directly for exactly one task per invocation (line 22: single taskId, line 29: no queue controller). GuiTaskRunner (the IRelayTaskRunner for Phase 2) is internal to VisualRelay.App and cannot be referenced by a console project. Headless users currently run a bash loop over RunTask (drive-v18.sh:16-22), paying full wall-clock for planning that could overlap, with no circuit breaker, NEEDS-REVIEW continuation, or drain summary log. ObservableRelayEventSink depends on Avalonia Dispatcher.UIThread and is not usable in a console app. No runtime errors exist — this is a capability gap, not a bug.",
  "excerpts": [
    "RelayQueueController.cs:35-50 — Two-phase constructor accepts planSubagentRunnerFactory, planTestRunner, planEventSinkFactory, lifecycle; only the GUI passes all four.",
    "RelayQueueController.cs:112-195 — Phase 1 parallel planning via PlanPhaseRunner.RunPlanPhaseAsync, gated by `if (_planSubagentRunnerFactory is not null && _planTestRunner is not null)`.",
    "MainWindowViewModel.Execution.cs:88-94 — Sole caller of two-phase constructor: `new RelayQueueController(RootPath, new GuiTaskRunner(...), planSubagentRunnerFactory: planSubagentFactory, planTestRunner: planTestRunner, planEventSinkFactory: planSinkFactory, lifecycle: lifecycle)`.",
    "tools/VisualRelay.RunTask/Program.cs:21-32 — Single task per invocation, RelayDriver directly, no RelayQueueController: `var taskId = filteredArgs[1]; var driver = new RelayDriver(dependencies, ...); await driver.RunTaskAsync(rootPath, taskId)`.",
    "GuiTaskRunner.cs:15 — `internal sealed class GuiTaskRunner` in VisualRelay.App; console project cannot reference it.",
    ".relay-scratch/drive-v18.sh:16-22 — Bash loop over RunTask confirms headless users have no drain: `for t in \"${TASKS[@]}\"; do dotnet run --project tools/VisualRelay.RunTask ... -- \"$ROOT\" \"$t\"; done`."
  ],
  "repro": "1. Open `src/VisualRelay.Core/Queue/RelayQueueController.cs` — observe DrainAsync with Phase 1 (parallel planning) and Phase 2 (serial execution).\n2. Open `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs:88-94` — observe it is the only caller that passes all four two-phase parameters.\n3. Open `tools/VisualRelay.RunTask/Program.cs:22,29` — observe it accepts exactly one taskId and creates RelayDriver directly, never touching RelayQueueController.\n4. Run `grep -r 'planSubagentRunnerFactory.*planTestRunner' src/ tools/` — only MainWindowViewModel.Execution.cs matches.\n5. Observe `.relay-scratch/drive-v18.sh` — headless automation is a bash loop over RunTask with no drain semantics."
}

## Stage 4 - Plan

{
  "plan": "Create a new `tools/VisualRelay.DrainQueue` console project (Option A — self-contained CLI mirroring GUI wiring) with 6 new source files and 1 test file. The tool loads the target repo's `.relay/config.json`, builds a two-phase `RelayQueueController` exactly as the GUI does (per-task `SwivalSubagentRunner` factory + `ShellTestRunner` for planning, `ConsoleTaskRunner` — an `IRelayTaskRunner` mirroring `GuiTaskRunner` — for Phase 2 execution with `CreateGitCommit:true, Resume:true`), then calls `RefreshAsync`, optionally rewrites the `Tasks` collection to enforce subset/order, and calls `DrainAsync`. Console output is attributable via a thread-safe `ConsoleRelayEventSink` that prefixes every line with `[taskId]`. Per-task `run.log` files are written via `FileRelayEventSink` (Phase 1 by `PlanPhaseRunner` internally, Phase 2 by `ConsoleTaskRunner`). `ArgParser` and `DrainOutcome` are small testable units with 14 unit tests covering argument validation, unknown-ID rejection, exit-code mapping, and summary formatting. The only existing file touched is `VisualRelay.slnx` to register the new project. Zero changes to any other existing source files.",
  "manifest": [
    "tools/VisualRelay.DrainQueue/VisualRelay.DrainQueue.csproj",
    "tools/VisualRelay.DrainQueue/Program.cs",
    "tools/VisualRelay.DrainQueue/ArgParser.cs",
    "tools/VisualRelay.DrainQueue/DrainOutcome.cs",
    "tools/VisualRelay.DrainQueue/ConsoleRelayEventSink.cs",
    "tools/VisualRelay.DrainQueue/ConsoleTaskRunner.cs",
    "VisualRelay.slnx",
    "tests/VisualRelay.Tests/DrainQueueToolTests.cs",
    "tests/VisualRelay.Tests/VisualRelay.Tests.csproj"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/DrainQueueToolTests.cs"
  ],
  "rationale": "33 tests across 4 groups: ArgParser.Parse (4), ArgParser.ValidateTaskIds (6), DrainOutcome.GetExitCode (6), DrainOutcome.FormatOutcomeLine (5), DrainOutcome.ComputeSummary (2), DrainOutcome.FormatSummary (2), controller-integration ordering/subsets (6), and NothingPendingMessage (2). The 26 NotImplementedException failures target exactly the 4 stub methods (Parse, ValidateTaskIds, GetExitCode, FormatOutcomeLine, ComputeSummary, FormatSummary) that the implementation stage must fill. The 7 passing tests confirm the existing controller seam (Tasks collection rewrite) works without any RelayQueueController changes, and the NothingPendingMessage constant is already defined. Every target behavior from the task is covered: unknown-id rejection, exit-code mapping (0 vs 2), outcome-line format matching RunTask, summary counts, empty-queue no-op, subset ordering, and two-phase drain with subset."
}

## Stage 6 - Implement

{
  "summary": "Implemented the headless-queue-drain-with-parallel-planning tool (6 files edited, 0 new files created). All stubs in `tools/VisualRelay.DrainQueue/` were filled with real implementations:\n\n- **ArgParser.cs**: `Parse()` parses root + optional taskIds; `ValidateTaskIds()` checks all requested IDs exist in pending set, returns error listing unknowns.\n- **DrainOutcome.cs**: `GetExitCode()` returns 0 for empty/committed, 2 if any flagged/failed. `FormatOutcomeLine()` matches RunTask format. `ComputeSummary()`/`FormatSummary()` provide counts.\n- **ConsoleRelayEventSink.cs**: Thread-safe console sink with `[taskId]` prefix for attributable interleaved output during parallel planning.\n- **ConsoleTaskRunner.cs**: Mirrors `GuiTaskRunner` — `ConsoleRelayEventSink` + `FileRelayEventSink` → `CompositeRelayEventSink` → `SwivalSubagentRunner` + `ShellTestRunner` → `RelayDriver(CreateGitCommit: true, Resume: true)`.\n- **Program.cs**: Thin entry point wiring `ArgParser` → `RelayConfigLoader.TryLoadAsync` → two-phase `RelayQueueController` → RefreshAsync → subset/order enforcement → DrainAsync → outcomes + summary → exit code.\n\nZero changes to existing source files outside the stubs. `VisualRelay.slnx` already had the project reference. All 33 `DrainQueueToolTests` pass (0 failed, 0 skipped)."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{"summary": "Review stage 7 returned verdict 'pass' with zero issues. Verified: build produces 0 warnings and 0 errors; all 33 DrainQueueToolTests pass (0 failed, 0 skipped). The solution file, test project reference, and all 6 source files are in place. No blockers or warnings to resolve."}

## Stage 9 - Verify

{
  "summary": "Created `tools/VisualRelay.DrainQueue` — a headless console project providing two-phase queue drain (parallel planning, serial execution) for CI/automation users. The tool loads target config via `RelayConfigLoader`, builds a two-phase `RelayQueueController` with per-task `SwivalSubagentRunner`+`ShellTestRunner` for planning and `ConsoleTaskRunner` (`RelayDriver`, `CreateGitCommit:true`, `Resume:true`) for execution, supports subset/order via `Tasks` collection rewrite after `RefreshAsync`, and outputs attributable `[taskId]`-prefixed console lines. 33 passing tests cover argument parsing, unknown-ID rejection, exit-code mapping (0 vs 2), outcome formatting, summary counts, empty-queue no-op, and controller integration. Zero changes to existing source outside the manifest.",
  "commitMessages": [
    "feat: add headless DrainQueue tool with two-phase parallel planning",
    "fix: handle empty-queue no-op with exit 0 and clear message",
    "test: add 33 unit/integration tests for DrainQueue tool"
  ]
}

## Stage 10 - Fix-verify (attempt 1/5)

{ "summary": "Split the oversized DrainQueueToolTests.cs (554 lines) into three partial class files, each ≤300 lines: DrainQueueToolTests.cs (143 lines, ArgParser tests), DrainQueueToolTests.DrainOutcome.cs (228 lines, GetExitCode/FormatOutcomeLine/ComputeSummary/FormatSummary/NothingPendingMessage tests), DrainQueueToolTests.ControllerIntegration.cs (203 lines, controller integration tests). Changed class declaration from `sealed class` to `sealed partial class`. All 610 tests pass, including the previously-failing SplitGuardVerificationTests.AllTestCsFiles_AreAtMost300Lines." }

## Stage 11 - Commit

Committed by Visual Relay.

