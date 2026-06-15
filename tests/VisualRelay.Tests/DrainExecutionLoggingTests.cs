using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the three gaps in parallel-planning drain:
/// Gap 1 — GuiTaskRunner doesn't wrap a FileRelayEventSink, so execute-phase
///         events (stages 5–11) are missing from run.log.
/// Gap 2 — planSubagentFactory creates SwivalSubagentRunner without eventSink,
///         so trace entries never reach the GUI during planning.
/// Gap 3 — RelayQueueController.DrainAsync excludes Planned outcomes from
///         results, so plannedCount in the GUI is always 0.
/// </summary>
public sealed class DrainExecutionLoggingTests
{
    // ── Gap 3: Planned outcomes excluded from results ──────────────────

    [Fact]
    public async Task DrainAsync_WhenPausedAfterPhase1_ReturnsPlannedOutcomes()
    {
        // 3 tasks, pause after first completes planning → all 3 Planned.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task-1", "# Task 1\n");
        repo.WriteTask("task-2", "# Task 2\n");
        repo.WriteTask("task-3", "# Task 3\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var planRunner = new ScriptedSubagentRunner();
        planRunner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var phase2Runner = new RecordingTaskRunner();

        RelayQueueController? ctrl = null;
        var count = 0;
        var lifecycle = new DrainLifecycleCallbacks
        {
            OnPlanningCompleted = (_, _) =>
            {
                // ReSharper disable once AccessToModifiedClosure — ctrl set below before DrainAsync fires this; ?. guards the impossible pre-assignment.
                if (Interlocked.Increment(ref count) == 1) ctrl?.RequestPause();
            }
        };

        ctrl = new RelayQueueController(repo.Root, phase2Runner,
            planSubagentRunnerFactory: _ => planRunner, planTestRunner: new ScriptedTestRunner(), lifecycle: lifecycle);
        await ctrl.RefreshAsync();

        var results = await ctrl.DrainAsync();

        Assert.Equal(RelayQueueState.Paused, ctrl.State);
        Assert.Empty(phase2Runner.TasksRun);
        // FAILS today: Planned outcomes excluded from results.
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(RelayTaskOutcomeStatus.Planned, r.Status));
    }

    [Fact]
    public async Task DrainAsync_WhenPausedAfterPhase1_IncludesMixedOutcomes()
    {
        // 2 tasks: one Flagged, one Planned. Both must appear in results.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("good", "# Good\n");
        repo.WriteTask("bad-plan", "# Bad plan\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var goodRunner = new ScriptedSubagentRunner();
        goodRunner.SeedHappyPath("src/good.cs", "tests/good.tests.cs");
        var badRunner = new FlagAtStageSubagentRunner(flagAtStage: 3);
        var phase2Runner = new RecordingTaskRunner();

        RelayQueueController? ctrl = null;
        var count = 0;
        var lifecycle = new DrainLifecycleCallbacks
        {
            OnPlanningCompleted = (_, _) =>
            {
                // ReSharper disable once AccessToModifiedClosure — ctrl set below before DrainAsync fires this; ?. guards the impossible pre-assignment.
                if (Interlocked.Increment(ref count) == 1) ctrl?.RequestPause();
            }
        };

        ctrl = new RelayQueueController(repo.Root, phase2Runner,
            planSubagentRunnerFactory: id => id == "bad-plan" ? badRunner : goodRunner,
            planTestRunner: new ScriptedTestRunner(), lifecycle: lifecycle);
        await ctrl.RefreshAsync();

        var results = await ctrl.DrainAsync();

        Assert.Equal(RelayQueueState.Paused, ctrl.State);
        Assert.Empty(phase2Runner.TasksRun);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r is { TaskId: "bad-plan", Status: RelayTaskOutcomeStatus.Flagged });
        // FAILS today — Planned outcomes excluded.
        Assert.Contains(results, r => r is { TaskId: "good", Status: RelayTaskOutcomeStatus.Planned });
    }

    [Fact]
    public async Task DrainAsync_PausedAfterPhase1_RunLogAndResultsIncludePlanned()
    {
        // Pause after Phase 1: run.log exists with planning events, results
        // include Planned outcome, no execute events in run.log.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("pause-me", "# Pause me\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/pause.cs", "tests/pause.tests.cs");
        var phase2Runner = new RecordingTaskRunner();

        RelayQueueController? ctrl = null;
        var lifecycle = new DrainLifecycleCallbacks
        {
            // ReSharper disable once AccessToModifiedClosure — ctrl set below before DrainAsync fires this; ?. guards the impossible pre-assignment.
            OnPlanningCompleted = (_, _) => ctrl?.RequestPause()
        };

        ctrl = new RelayQueueController(repo.Root, phase2Runner,
            planSubagentRunnerFactory: _ => runner,
            planTestRunner: new ScriptedTestRunner(), lifecycle: lifecycle);
        await ctrl.RefreshAsync();

        var results = await ctrl.DrainAsync();

        Assert.Equal(RelayQueueState.Paused, ctrl.State);
        Assert.Empty(phase2Runner.TasksRun);
        // FAILS today: Planned outcome excluded.
        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Planned, results[0].Status);
        Assert.Equal("pause-me", results[0].TaskId);

        // run.log must exist with planning events, no execute events.
        var runLogPath = Path.Combine(repo.Root, ".relay", "pause-me", "run.log");
        Assert.True(File.Exists(runLogPath));
        var runLog = await File.ReadAllTextAsync(runLogPath);
        Assert.Contains("stage_start", runLog, StringComparison.Ordinal);
        Assert.DoesNotContain("s5/", runLog, StringComparison.Ordinal);

        // drain summary log has plan milestones.
        var drainLogs = Directory.GetFiles(Path.Combine(repo.Root, ".relay"), "drain-*.log");
        Assert.Single(drainLogs);
    }

    // ── Gap 1: Execute-phase events missing from run.log ───────────────

    [Fact]
    public async Task DrainAsync_ExecutePhase_AppendsEventsToRunLog()
    {
        // Phase 1 (PlanPhaseRunner) writes run.log in worktree, copies back.
        // Phase 2 uses a FileRelayEventSink (the fixed GuiTaskRunner pattern),
        // so execute-phase events (stages 5–11) are appended to run.log.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("full-run", "# Full run\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 1);

        // Phase 1: plan
        var planResults = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root, tasks: [("full-run", runner)],
            config: config, testRunner: new ScriptedTestRunner());
        Assert.Single(planResults);
        Assert.Equal(RelayTaskOutcomeStatus.Planned, planResults[0].Outcome.Status);

        var runLogPath = Path.Combine(repo.Root, ".relay", "full-run", "run.log");
        Assert.True(File.Exists(runLogPath));

        // Phase 2: execute — with FileRelayEventSink appending to run.log
        // (the fixed GuiTaskRunner pattern).
        var executeObs = new InMemoryRelayEventSink();
        var fileSink = new FileRelayEventSink(runLogPath);
        var sink = new CompositeRelayEventSink(executeObs, fileSink);
        var executeDeps = RelayDriverDependencies.ForTests(runner,
            new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
            sink);
        var executeDriver = new RelayDriver(executeDeps,
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));
        var outcome = await executeDriver.RunTaskAsync(repo.Root, "full-run");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Driver produced events to the observable sink.
        Assert.Contains(executeObs.Events, e => e.StageNumber == 5);

        // Execute events ARE in run.log (FileRelayEventSink in use).
        var finalLog = await File.ReadAllTextAsync(runLogPath);
        Assert.Contains("s5/", finalLog, StringComparison.Ordinal);
        Assert.Contains("s11/", finalLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutePhase_WithFileSink_AppendsToExistingRunLog()
    {
        // CompositeRelayEventSink with FileRelayEventSink appends execute
        // events to planning run.log. Also verifies subagent trace events
        // land in run.log when the subagent is wired to the composite sink.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("correct", "# Correct\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var inner = new ScriptedSubagentRunner();
        inner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 1);

        await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root, tasks: [("correct", inner)],
            config: config, testRunner: new ScriptedTestRunner());

        var runLogPath = Path.Combine(repo.Root, ".relay", "correct", "run.log");
        Assert.True(File.Exists(runLogPath));
        var planSize = new FileInfo(runLogPath).Length;

        // Phase 2 with a TraceEmittingSubagentRunner wired to the composite
        // sink — verifies both driver events and subagent traces land in run.log.
        var obs = new InMemoryRelayEventSink();
        var fileSink = new FileRelayEventSink(runLogPath);
        var sink = new CompositeRelayEventSink(obs, fileSink);
        var traceRunner = new TraceEmittingSubagentRunner(inner, traceSink: sink);
        var deps = RelayDriverDependencies.ForTests(traceRunner,
            new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
            sink);
        var driver = new RelayDriver(deps,
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));
        var outcome = await driver.RunTaskAsync(repo.Root, "correct");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // run.log grew (appended, not overwritten), has driver events and subagent traces.
        Assert.True(new FileInfo(runLogPath).Length > planSize);
        var log = await File.ReadAllTextAsync(runLogPath);
        Assert.Contains("s5/", log, StringComparison.Ordinal);
        Assert.Contains("s11/", log, StringComparison.Ordinal);
        Assert.Contains("trace for correct", log, StringComparison.Ordinal);
    }

    // ── Gap 2: Trace events missing during planning ────────────────────

    [Fact]
    public async Task PlanPhaseRunner_TraceEvents_DeliveredToEventSink()
    {
        // The fixed planSubagentFactory now passes an ObservableRelayEventSink
        // to SwivalSubagentRunner, so trace events reach the GUI event sink.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("trace-me", "# Trace me\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var captured = new InMemoryRelayEventSink();
        var inner = new ScriptedSubagentRunner();
        inner.SeedHappyPath("src/traced.cs", "tests/traced.tests.cs");
        // Fixed: traceSink is non-null — trace events are delivered.
        var traceRunner = new TraceEmittingSubagentRunner(inner, traceSink: captured);
        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 1);

        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root, tasks: [("trace-me", traceRunner)],
            config: config, testRunner: new ScriptedTestRunner(),
            eventSinkFactory: _ => captured);

        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Planned, results[0].Outcome.Status);

        // Driver events reach the sink.
        Assert.Contains(captured.Events, e =>
            e.EventName is "stage_start" or "stage_done");

        // Trace events ARE delivered because traceSink is non-null.
        Assert.Contains(captured.Events, e =>
            e is { EventName: "trace_entry", Data: not null }
            && e.Data.TryGetValue("content", out var c)
            && c.Contains("trace for trace-me", StringComparison.Ordinal));
    }

    // ── Legacy path regression ─────────────────────────────────────────

    [Fact]
    public async Task DrainAsync_LegacyConstructor_SerialOnly_NoRegression()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("legacy", "# Legacy\n");

        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, results[0].Status);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        Assert.Equal(["legacy"], runner.TasksRun);
    }
}
