using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.Core.Agent;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// The resilience half of the loop's coverage: what it does about a repeated
/// call, a failure worth retrying, a failure that is not, a model that spends
/// its whole budget reasoning, a context that outgrows the window, and a stage
/// that gets cancelled.
/// </summary>
public sealed partial class AgentTurnLoopTests
{
    /// <summary>
    /// A model looping on one identical call is warned, then refused, and the
    /// refusal is counted where the report can see it.
    /// </summary>
    [Fact]
    public async Task RepeatedIdenticalCalls_AreEventuallyRefused()
    {
        var tool = new StubTool("read_file", ToolResult.Ok("same every time"));
        var transport = new ScriptedModelTransport();
        for (var i = 0; i < 5; i++) transport.CallsTool("read_file", """{"path":"a"}""");
        transport.Answer("gave up");
        var (loop, sink) = Build(transport, tool);

        var result = await RunAsync(loop);

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        Assert.True(result.Stats.StormedCalls > 0, "the storm breaker should have refused a call");
        // Warned before refusing, so a deliberate re-run stays possible.
        Assert.Contains(sink.Events, e =>
            e.Kind == AgentEventKind.StormIntervention && e.Detail == "Warn");
        Assert.Contains(sink.Events, e =>
            e.Kind == AgentEventKind.StormIntervention && e.Detail == "Suppress");
        // A refused call never reaches the tool.
        Assert.True(tool.Calls.Count < 5);
    }

    /// <summary>A retryable provider failure is retried and can then succeed.</summary>
    [Fact]
    public async Task RetryableFailure_IsRetriedAndCanSucceed()
    {
        var transport = new ScriptedModelTransport()
            .Fails(503, """{"error":{"message":"upstream unavailable"}}""")
            .Answer("second time lucky");
        var (loop, sink) = Build(transport);

        var result = await RunAsync(loop, NoBackoff());

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        Assert.Equal("second time lucky", result.Answer);
        Assert.Equal(1, result.Stats.RecoveredResponses);
        Assert.Contains(sink.Events, e => e.Kind == AgentEventKind.Retry);
    }

    /// <summary>
    /// An exhausted quota arrives as a 429 but is not retryable, so the loop
    /// stops at once instead of spending the retry budget on a call that cannot
    /// succeed.
    /// </summary>
    [Fact]
    public async Task QuotaExhaustion_StopsImmediately()
    {
        var transport = new ScriptedModelTransport()
            .Fails(429, """{"error":{"code":"1113","message":"balance not enough"}}""");
        var (loop, sink) = Build(transport);

        var result = await RunAsync(loop, NoBackoff());

        Assert.Equal(AgentLoopOutcome.Error, result.Outcome);
        Assert.Equal(1, result.Stats.LlmCalls);
        Assert.DoesNotContain(sink.Events, e => e.Kind == AgentEventKind.Retry);
        Assert.Contains("balance", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>An auth failure likewise stops at once rather than retrying.</summary>
    [Fact]
    public async Task AuthFailure_StopsImmediately()
    {
        var transport = new ScriptedModelTransport()
            .Fails(401, """{"error":"Invalid username or password."}""");
        var (loop, _) = Build(transport);

        var result = await RunAsync(loop, NoBackoff());

        Assert.Equal(AgentLoopOutcome.Error, result.Outcome);
        Assert.Equal(1, result.Stats.LlmCalls);
    }

    /// <summary>
    /// The GLM shape: a model that spends its whole output budget on reasoning
    /// is retried with a larger budget, not reported as an empty answer and not
    /// escalated to a dearer tier over what is only a budget problem.
    /// </summary>
    [Fact]
    public async Task ReasoningWithoutAnswer_IsRetriedWithABiggerBudget()
    {
        var transport = new ScriptedModelTransport()
            .ReasonsWithoutAnswering()
            .Answer("now with room to answer");
        var (loop, _) = Build(transport);

        var result = await RunAsync(loop, NoBackoff());

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        Assert.Equal("now with room to answer", result.Answer);
        // The first attempt sent no ceiling; the retry raised one explicitly.
        Assert.DoesNotContain("max_tokens", transport.Requests[0], StringComparison.Ordinal);
        Assert.Contains("max_tokens", transport.Requests[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Compaction triggers on the provider's own measured input tokens, drops
    /// the oldest tool results, and is counted in the report.
    /// </summary>
    [Fact]
    public async Task Compaction_TriggersOnMeasuredTokensAndDropsOldToolResults()
    {
        var tool = new StubTool("read_file", ToolResult.Ok("contents"));
        var transport = new ScriptedModelTransport();
        for (var i = 0; i < 14; i++) transport.CallsTool("read_file", $$"""{"path":"f{{i}}"}""");
        transport.Answer("done");
        var (loop, sink) = Build(transport, tool);

        // A window of 12 tokens against the scripted 10 prompt tokens per call
        // puts every turn past the 80% threshold.
        var result = await RunAsync(loop, Options(maxTurns: 20) with { ContextWindow = 12 });

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        Assert.True(result.Stats.Compactions > 0, "compaction should have fired");
        Assert.Contains(sink.Events, e => e.Kind == AgentEventKind.Compaction);
    }

    /// <summary>
    /// Compaction is capped, so a stage cannot spend its whole life compacting.
    /// </summary>
    [Fact]
    public void Compaction_IsCapped()
    {
        var compactor = new ContextCompactor(
            contextWindow: 100, new AgentResilienceOptions(MaxCompactions: 2));
        var messages = BuildConversation(40);

        for (var i = 0; i < 5; i++)
        {
            if (compactor.ShouldCompact(measuredPromptTokens: 95))
                messages = [.. compactor.Compact(messages).Messages];
        }

        Assert.Equal(2, compactor.Compactions);
        Assert.False(compactor.ShouldCompact(measuredPromptTokens: 95));
    }

    /// <summary>Nothing measured yet is never a reason to compact.</summary>
    [Fact]
    public void Compaction_NeverFiresOnAnUnmeasuredConversation()
    {
        var compactor = new ContextCompactor(contextWindow: 100);

        Assert.False(compactor.ShouldCompact(measuredPromptTokens: 0));
    }

    /// <summary>
    /// Compaction keeps the opening message, which carries the stage contract,
    /// and never orphans a tool result from the assistant turn that requested it.
    /// </summary>
    [Fact]
    public void Compaction_KeepsTheContractAndOrphansNothing()
    {
        var compactor = new ContextCompactor(contextWindow: 100);
        var messages = BuildConversation(30);

        var compacted = compactor.Compact(messages);

        Assert.True(compacted.DroppedMessages > 0);
        Assert.Equal("system", compacted.Messages[0].Role);
        Assert.Contains("context compacted", compacted.Note!, StringComparison.Ordinal);
        // No surviving tool message may cite a call id whose assistant turn went.
        var liveCallIds = compacted.Messages
            .SelectMany(m => m.ToolCalls ?? [])
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var toolMessage in compacted.Messages.Where(m => m.Role == "tool"))
            Assert.Contains(toolMessage.ToolCallId!, liveCallIds);
    }

    /// <summary>
    /// Cancellation produces its own outcome, and therefore a report. The old
    /// runner computed "interrupted" and re-raised without persisting, which is
    /// why 1109 archived reports contain not one of them.
    /// </summary>
    [Fact]
    public async Task Cancellation_ProducesAnOutcomeAndSoAReport()
    {
        var transport = new ScriptedModelTransport().Answer("never reached");
        var (loop, _) = Build(transport);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await loop.RunAsync(
            [new ChatMessage("user", "do the thing")],
            Options(),
            new ToolContext("/repo", TimeSpan.FromMinutes(30)),
            cts.Token);

        Assert.Equal(AgentLoopOutcome.Cancelled, result.Outcome);
        Assert.Equal("interrupted", result.OutcomeName);
        Assert.Equal(1, result.ExitCode);
        Assert.NotNull(result.Stats);
    }

    /// <summary>A tool that throws is reported to the model, not out of the loop.</summary>
    [Fact]
    public async Task AThrowingTool_IsReportedToTheModel()
    {
        var transport = new ScriptedModelTransport()
            .CallsTool("explode", """{"path":"a"}""")
            .Answer("recovered");
        var (loop, _) = Build(transport, new ThrowingTool());

        var result = await RunAsync(loop);

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        Assert.Equal(1, result.Stats.ToolCallsFailed);
        Assert.Contains("failed unexpectedly", transport.Requests[1], StringComparison.Ordinal);
    }

    private static AgentLoopOptions NoBackoff() => Options() with { RetryBackoffBase = TimeSpan.Zero };

    private static List<ChatMessage> BuildConversation(int turns)
    {
        var messages = new List<ChatMessage> { new("system", "stage contract") };
        for (var i = 0; i < turns; i++)
        {
            var id = $"c{i}";
            messages.Add(new ChatMessage("assistant", null,
                ToolCalls: [new ToolCall(id, "read_file", """{"path":"x"}""")]));
            messages.Add(new ChatMessage("tool", new string('x', 200), ToolCallId: id));
        }

        return messages;
    }

    /// <summary>A tool that throws, to prove the loop contains the failure.</summary>
    private sealed class ThrowingTool : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(
            "explode", "always throws",
            JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""")!);

        public Task<ToolResult> InvokeAsync(
            JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
