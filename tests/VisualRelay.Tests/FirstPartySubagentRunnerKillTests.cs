using VisualRelay.Core.Agent;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// What a stage's watchdog does to a real loop. Measured on the Windows arm with crawl: the
/// research stage died 600 s after its last event, while its <c>make</c> was still inside its
/// own 600 s timeout, and reached the driver as "stage cancelled" with no kill signature, so
/// it was flagged without the escalation a stall earns. The loop turns every cancellation
/// into a Cancelled result instead of throwing, and the supervisor only recognised a kill
/// that arrived as an exception.
/// </summary>
public sealed class FirstPartySubagentRunnerKillTests
{
    private sealed class Sink : IAgentEventSink
    {
        public void Publish(AgentEvent agentEvent) { }
    }

    /// <summary>A tool that runs until the test releases it or the stage is cancelled.</summary>
    private sealed class HeldTool : IAgentTool
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolDefinition Definition { get; } = new(
            "build", "Runs a long build.",
            System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object","properties":{}}""")!);

        public async Task<ToolResult> InvokeAsync(
            System.Text.Json.JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ToolResult("exit code 0");
        }
    }

    private static StageInvocation Invocation(string root, TimeSpan ceiling) =>
        new(
            Stage: RelayStages.All[0],
            Tier: "cheap",
            RunId: "run-1",
            TargetRoot: root,
            TaskName: "a-task",
            TaskInput: "do the thing",
            LedgerSoFar: "(none)",
            Manifest: [],
            LogSources: [],
            TraceDirectory: root,
            ReportFile: Path.Combine(root, "stage1-attempt1.report.json"),
            MaxTurns: 4,
            AbsoluteCeilingMs: (int)ceiling.TotalMilliseconds);

    private static FirstPartySubagentRunner Build(ScriptedModelTransport transport, HeldTool tool, ManualTimeProvider clock) =>
        new(transport, RelayConfigLoader.Defaults(),
            new DictionaryEnvironmentAccessor { ["DEEPSEEK_API_KEY"] = "sk-test-value" },
            _ => new Sink(), tools: [tool], timeProvider: clock, retryBackoffBase: TimeSpan.Zero);

    [Fact]
    public async Task AWatchdogKill_ReachesTheResultWithItsSignature()
    {
        using var repo = TestRepository.Create();
        var clock = new ManualTimeProvider();
        var tool = new HeldTool();
        var transport = new ScriptedModelTransport().CallsTool("build", "{}");

        var run = Build(transport, tool, clock).RunAsync(Invocation(repo.Root, TimeSpan.FromSeconds(60)));
        await tool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(61));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("absolute_ceiling", result.Kill?.Reason);
        Assert.True(result.HardAbort);
        Assert.Contains("the stage stalled", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandLongerThanEveryStallWindow_FinishesAndTheStageGoesOn()
    {
        using var repo = TestRepository.Create();
        var clock = new ManualTimeProvider();
        var tool = new HeldTool();
        var transport = new ScriptedModelTransport().CallsTool("build", "{}").Answer("done").Answer("done");

        var run = Build(transport, tool, clock).RunAsync(Invocation(repo.Root, TimeSpan.FromHours(1)));
        await tool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The watchdog's tick lands while the build is still running, past every stall window.
        clock.Advance(TimeSpan.FromMinutes(25));
        tool.Release.TrySetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(result.Kill);
        Assert.True(transport.Requests.Count >= 2, $"the model was asked again after the build; requests: {transport.Requests.Count}");
    }
}
