using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.Core.Agent;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Drives the agent loop against a model scripted turn by turn. Half of what
/// matters here is what the loop SENDS after a fault, so the captured requests
/// are asserted as well as the results.
/// </summary>
public sealed partial class AgentTurnLoopTests
{
    /// <summary>Collects the loop's event stream for assertions.</summary>
    private sealed class RecordingSink : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];

        public void Publish(AgentEvent agentEvent) => Events.Add(agentEvent);
    }

    /// <summary>A tool that records its calls and returns a canned result.</summary>
    private sealed class StubTool(string name, ToolResult result, JsonNode? schema = null) : IAgentTool
    {
        public List<string> Calls { get; } = [];

        public ToolDefinition Definition { get; } = new(
            name,
            $"stub tool {name}",
            schema ?? JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""")!);

        public Task<ToolResult> InvokeAsync(
            JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToString());
            return Task.FromResult(result);
        }
    }

    private static AgentLoopOptions Options(int maxTurns = 10, AgentResilienceOptions? resilience = null) =>
        new(
            Model: "fake-1",
            Endpoint: new Uri("https://provider.test/v1/chat/completions"),
            Headers: new Dictionary<string, string>(),
            Capabilities: ProviderCapabilityCatalog.For("DeepSeek"),
            MaxTurns: maxTurns,
            StageBudget: TimeSpan.FromMinutes(30),
            Resilience: resilience,
            RetryBudget: 2);

    private static (AgentTurnLoop Loop, RecordingSink Sink) Build(
        ScriptedModelTransport transport, params IAgentTool[] tools)
    {
        var sink = new RecordingSink();
        var client = new ChatCompletionClient(transport);
        return (new AgentTurnLoop(client, tools, sink), sink);
    }

    private static Task<AgentLoopResult> RunAsync(
        AgentTurnLoop loop, AgentLoopOptions? options = null) =>
        loop.RunAsync(
            [new ChatMessage("user", "do the thing")],
            options ?? Options(),
            new ToolContext("/repo", TimeSpan.FromMinutes(30)));

    /// <summary>A model that answers immediately succeeds in one turn.</summary>
    [Fact]
    public async Task ImmediateAnswer_SucceedsInOneTurn()
    {
        var transport = new ScriptedModelTransport().Answer("all done");
        var (loop, _) = Build(transport);

        var result = await RunAsync(loop);

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        Assert.Equal("all done", result.Answer);
        Assert.Equal(1, result.Stats.Turns);
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// The answer comes back as a typed result. Nothing writes a fenced JSON
    /// block to stdout for something else to fish back out.
    /// </summary>
    [Fact]
    public async Task TheAnswer_IsTypedNotExtractedFromStdout()
    {
        var transport = new ScriptedModelTransport().Answer("""{"summary":"done"}""");
        var (loop, _) = Build(transport);

        var result = await RunAsync(loop);

        Assert.Equal("""{"summary":"done"}""", result.Answer);
        Assert.Equal("fake-1", result.ServedModel);
    }

    /// <summary>A tool call runs, and its result is fed back for the next turn.</summary>
    [Fact]
    public async Task ToolCall_RunsAndItsResultIsFedBack()
    {
        var tool = new StubTool("read_file", ToolResult.Ok("file contents"));
        var transport = new ScriptedModelTransport()
            .CallsTool("read_file", """{"path":"a.txt"}""")
            .Answer("read it");
        var (loop, _) = Build(transport, tool);

        var result = await RunAsync(loop);

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Stats.Turns);
        Assert.Equal(1, result.Stats.ToolCallsSucceeded);
        Assert.Equal("""{"path":"a.txt"}""", Assert.Single(tool.Calls));
        Assert.Contains("file contents", transport.Requests[1], StringComparison.Ordinal);
    }

    /// <summary>Per-tool counts land in the stats the report carries.</summary>
    [Fact]
    public async Task PerToolCounts_ReachTheStats()
    {
        var good = new StubTool("read_file", ToolResult.Ok("ok"));
        var bad = new StubTool("grep", ToolResult.Error("no match"));
        var transport = new ScriptedModelTransport()
            .CallsTool("read_file", """{"path":"a"}""")
            .CallsTool("grep", """{"path":"b"}""")
            .Answer("done");
        var (loop, _) = Build(transport, good, bad);

        var result = await RunAsync(loop);

        Assert.Equal(1, result.Stats.ToolCallsByName["read_file"].Succeeded);
        Assert.Equal(1, result.Stats.ToolCallsByName["grep"].Failed);
        Assert.Equal(2, result.Stats.ToolCallsTotal);
    }

    /// <summary>
    /// An unknown tool name is answered with the nearest match rather than an
    /// exception, so the model can correct itself on the next turn.
    /// </summary>
    [Fact]
    public async Task UnknownToolName_IsCorrectedNotThrown()
    {
        var tool = new StubTool("read_file", ToolResult.Ok("ok"));
        var transport = new ScriptedModelTransport()
            .CallsTool("read_files", """{"path":"a"}""")
            .Answer("recovered");
        var (loop, sink) = Build(transport, tool);

        var result = await RunAsync(loop);

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        var unknown = Assert.Single(sink.Events, e => e.Kind == AgentEventKind.UnknownTool);
        Assert.Contains("Did you mean 'read_file'", unknown.Text!, StringComparison.Ordinal);
        // The body escapes apostrophes, so assert on the part that survives.
        Assert.Contains("Did you mean", transport.Requests[1], StringComparison.Ordinal);
    }

    /// <summary>Damaged arguments are repaired and the tool still runs.</summary>
    [Fact]
    public async Task TruncatedArguments_AreRepairedAndTheToolStillRuns()
    {
        var tool = new StubTool("read_file", ToolResult.Ok("ok"));
        var transport = new ScriptedModelTransport()
            .CallsTool("read_file", """{"path":"a.txt""")
            .Answer("done");
        var (loop, sink) = Build(transport, tool);

        var result = await RunAsync(loop);

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        Assert.Equal(1, result.Stats.TruncationRepairs);
        Assert.Contains(sink.Events, e => e.Kind == AgentEventKind.ArgumentRepair);
        Assert.Single(tool.Calls);
    }

    /// <summary>Arguments beyond repair are refused with an actionable message.</summary>
    [Fact]
    public async Task UnrepairableArguments_AreRefusedWithAdvice()
    {
        var tool = new StubTool("read_file", ToolResult.Ok("ok"));
        var transport = new ScriptedModelTransport()
            .CallsTool("read_file", "I will read the file")
            .Answer("recovered");
        var (loop, _) = Build(transport, tool);

        var result = await RunAsync(loop);

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        Assert.Empty(tool.Calls);
        Assert.Contains("well-formed JSON object", transport.Requests[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Running out of turns is its own outcome with exit code 2, which the driver
    /// reads directly instead of inferring it.
    /// </summary>
    [Fact]
    public async Task TurnBudgetExhaustion_IsItsOwnOutcome()
    {
        var tool = new StubTool("read_file", ToolResult.Ok("ok"));
        var transport = new ScriptedModelTransport();
        for (var i = 0; i < 3; i++) transport.CallsTool("read_file", $$"""{"path":"{{i}}"}""");
        var (loop, _) = Build(transport, tool);

        var result = await RunAsync(loop, Options(maxTurns: 3));

        Assert.Equal(AgentLoopOutcome.Exhausted, result.Outcome);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("exhausted", result.OutcomeName);
        Assert.Contains("turn budget", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tool events carry the name and the real elapsed duration, which is what
    /// the trace and the timeline are built from. The old path had neither: the
    /// trace was written at process exit with every record stamped milliseconds
    /// apart, so per-call timing did not exist.
    /// </summary>
    [Fact]
    public async Task ToolEvents_CarryTheNameAndARealDuration()
    {
        var tool = new StubTool("read_file", ToolResult.Ok("contents"));
        var transport = new ScriptedModelTransport()
            .CallsTool("read_file", """{"path":"a.txt"}""")
            .Answer("done");
        var (loop, sink) = Build(transport, tool);

        await RunAsync(loop);

        var started = Assert.Single(sink.Events, e => e.Kind == AgentEventKind.ToolCallStarted);
        var finished = Assert.Single(sink.Events, e => e.Kind == AgentEventKind.ToolCallFinished);

        Assert.Equal("read_file", started.ToolName);
        Assert.Equal("read_file", finished.ToolName);
        Assert.Contains("a.txt", started.Text!, StringComparison.Ordinal);
        Assert.NotNull(finished.Duration);
        Assert.Equal("ok", finished.Detail);
    }

    /// <summary>
    /// The usage event carries the measured numbers and the concrete model that
    /// served them, which is what cost is attributed to.
    /// </summary>
    [Fact]
    public async Task UsageEvents_CarryTheNumbersAndTheServingModel()
    {
        var transport = new ScriptedModelTransport().Answer("done");
        var (loop, sink) = Build(transport);

        await RunAsync(loop);

        var usage = Assert.Single(sink.Events, e => e.Kind == AgentEventKind.Usage);
        Assert.Equal("fake-1", usage.Model);
        Assert.NotNull(usage.Usage);
        Assert.Equal(10, usage.Usage!.PromptTokens);
        Assert.Equal(4, usage.Usage.CompletionTokens);
    }

    /// <summary>Measured usage accumulates across turns rather than being estimated.</summary>
    [Fact]
    public async Task Usage_IsMeasuredAndAccumulated()
    {
        var tool = new StubTool("read_file", ToolResult.Ok("ok"));
        var transport = new ScriptedModelTransport()
            .CallsTool("read_file", """{"path":"a"}""")
            .Answer("done");
        var (loop, sink) = Build(transport, tool);

        var result = await RunAsync(loop);

        Assert.Equal(20, result.Stats.PromptTokens);
        Assert.Equal(10, result.Stats.CompletionTokens);
        Assert.Equal(2, result.Stats.LlmCalls);
        Assert.Equal(2, sink.Events.Count(e => e.Kind == AgentEventKind.Usage));
    }
}
