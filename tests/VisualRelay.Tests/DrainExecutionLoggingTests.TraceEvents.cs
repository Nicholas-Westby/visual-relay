using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class DrainExecutionLoggingTests
{
    [Fact]
    public async Task PlanPhaseRunner_TraceEvents_DeliveredToEventSink()
    {
        // PlanPhaseRunner hardcodes a real GitInvoker for worktree creation (no
        // injection seam) — this fact is irreducibly bound to the real git binary.
        SlowIntegration.SkipIfNotOptedIn();
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
            eventSinkFactory: _ => captured,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);

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
}
