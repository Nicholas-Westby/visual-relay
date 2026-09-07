using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class DrainExecutionLoggingTests
{
    [Fact]
    public async Task PlanPhaseRunner_TraceEvents_DeliveredToEventSink()
    {
        // The fixed planSubagentFactory now passes an ObservableRelayEventSink
        // to SandboxedStage, so trace events reach the GUI event sink.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("trace-me", "# Trace me\n");
        var sim = PlanPhaseTestHelpers.InitGitSim(repo.Root);

        var captured = new InMemoryRelayEventSink();
        var inner = new ScriptedSubagentRunner();
        inner.SeedHappyPath("src/traced.cs", "tests/traced.tests.cs");
        // Fixed: traceSink is non-null — trace events are delivered.
        var traceRunner = new TraceEmittingSubagentRunner(inner, traceSink: captured);
        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 1);

        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root, tasks: [("trace-me", _ => traceRunner)], config: config, testRunner: new ScriptedTestRunner(), eventSinkFactory: _ => captured, environmentAccessor: PlanPhaseTestHelpers.TempXdg, gitInvoker: sim);

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

    /// <summary>
    /// The planning stages get the same run log as every other stage. Each planning
    /// agent is built FROM the sink the planning driver publishes to — the one that
    /// writes the task's run.log — so its tool calls and thinking are readable
    /// afterwards. Built from a sink handed to the caller earlier, the agent published
    /// to the GUI alone and stages 1-4 left a log with nothing but stage boundaries,
    /// though they are where most of a task's spend happens.
    /// </summary>
    [Fact]
    public async Task PlanPhaseRunner_TraceEvents_ReachTheTaskRunLog()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("trace-log", "# Trace me\n");
        var sim = PlanPhaseTestHelpers.InitGitSim(repo.Root);

        var inner = new ScriptedSubagentRunner();
        inner.SeedHappyPath("src/traced.cs", "tests/traced.tests.cs");
        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 1);

        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("trace-log", sink => new TraceEmittingSubagentRunner(inner, sink))],
            config: config, testRunner: new ScriptedTestRunner(),
            environmentAccessor: PlanPhaseTestHelpers.TempXdg, gitInvoker: sim);

        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Planned, results[0].Outcome.Status);

        var runLog = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "trace-log", "run.log"));
        Assert.Contains("trace_entry", runLog, StringComparison.Ordinal);
        Assert.Contains("trace for trace-log", runLog, StringComparison.Ordinal);
    }
}
