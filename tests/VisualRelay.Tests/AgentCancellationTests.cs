using System.Text;
using VisualRelay.Core.Agent;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Cancellation, which the spec's matrix names four ways: mid-stream, before the
/// first byte, during tool execution, and twice over.
/// <para>
/// A user stop and a watchdog kill stay separate tokens — the watchdog cancels a
/// linked source so the loop stops without the caller's own token being touched.
/// What both must share is the exit: the loop CATCHES the cancellation and
/// reports <c>Cancelled</c> rather than throwing, so the caller gets one outcome
/// and never an <see cref="ObjectDisposedException"/> from a disposal race.
/// </para>
/// </summary>
public sealed class AgentCancellationTests
{
    private sealed class Sink : IAgentEventSink
    {
        public void Publish(AgentEvent agentEvent) { }
    }

    /// <summary>
    /// Waits forever, ending only on cancellation. Deliberately not
    /// <c>Task.Delay(Timeout.Infinite)</c>: that is a real sleep, and the point
    /// here is a task that never elapses on its own.
    /// </summary>
    /// <param name="token">The token that ends the wait.</param>
    /// <returns>A task that only ever cancels.</returns>
    private static Task BlockUntilCancelled(CancellationToken token)
    {
        var blocked = new TaskCompletionSource();
        token.Register(() => blocked.TrySetCanceled(token));
        return blocked.Task;
    }

    /// <summary>A transport that hands control back before writing anything.</summary>
    private sealed class BlockingTransport(TaskCompletionSource gate, bool emitFirst)
        : IProviderTransport
    {
        public Task<ProviderResponse> SendAsync(
            ProviderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderResponse(200, new Dictionary<string, string>(), string.Empty));

        public Task<ProviderStreamResponse> StreamAsync(
            ProviderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStreamResponse(
                200, new Dictionary<string, string>(), Read));

        private async IAsyncEnumerable<ReadOnlyMemory<byte>> Read(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            if (emitFirst)
            {
                yield return Encoding.UTF8.GetBytes(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n");
                gate.TrySetResult();
            }
            else
            {
                gate.TrySetResult();
            }

            // Never writes again; only cancellation ends this.
            await BlockUntilCancelled(cancellationToken).ConfigureAwait(false);
            yield break;
        }
    }

    /// <summary>A tool that blocks until cancelled, so a stop lands inside it.</summary>
    private sealed class BlockingTool(TaskCompletionSource entered) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(
            "block", "Blocks until cancelled.",
            System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object","properties":{}}""")!);

        public async Task<ToolResult> InvokeAsync(
            System.Text.Json.JsonElement arguments, ToolContext context,
            CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await BlockUntilCancelled(cancellationToken).ConfigureAwait(false);
            return new ToolResult(string.Empty, false);
        }
    }

    private static AgentLoopOptions Options() =>
        new(
            Model: "fake-1",
            Endpoint: new Uri("https://provider.test/v1/chat/completions"),
            Headers: new Dictionary<string, string>(),
            Capabilities: ProviderCapabilityCatalog.For("DeepSeek"),
            MaxTurns: 6,
            StageBudget: TimeSpan.FromMinutes(30),
            RetryBudget: 0,
            RetryBackoffBase: TimeSpan.Zero);

    private static Task<AgentLoopResult> RunAsync(
        IProviderTransport transport, CancellationToken token, params IAgentTool[] tools) =>
        new AgentTurnLoop(new ChatCompletionClient(transport), tools, new Sink())
            .RunAsync(
                [new ChatMessage("user", "go")], Options(),
                new ToolContext(Path.GetTempPath(), TimeSpan.FromMinutes(5)), token);

    /// <summary>
    /// Cancelling after content has arrived ends the turn as cancelled rather
    /// than as a completed answer built from a partial stream.
    /// </summary>
    [Fact]
    public async Task CancelMidStream_EndsAsCancelled()
    {
        var gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var run = RunAsync(new BlockingTransport(gate, emitFirst: true), cts.Token);
        await gate.Task;
        await cts.CancelAsync();

        Assert.Equal(AgentLoopOutcome.Cancelled, (await run).Outcome);
    }

    /// <summary>Cancelling before a single byte arrives ends the same way.</summary>
    [Fact]
    public async Task CancelBeforeFirstByte_EndsAsCancelled()
    {
        var gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var run = RunAsync(new BlockingTransport(gate, emitFirst: false), cts.Token);
        await gate.Task;
        await cts.CancelAsync();

        Assert.Equal(AgentLoopOutcome.Cancelled, (await run).Outcome);
    }

    /// <summary>
    /// A stop that lands while a tool is running cancels the tool too, rather
    /// than waiting out a command that may never return.
    /// </summary>
    [Fact]
    public async Task CancelDuringToolExecution_EndsAsCancelled()
    {
        var entered = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var transport = new ScriptedModelTransport().CallsTool("block", "{}");

        var run = RunAsync(transport, cts.Token, new BlockingTool(entered));
        await entered.Task;
        await cts.CancelAsync();

        Assert.Equal(AgentLoopOutcome.Cancelled, (await run).Outcome);
    }

    /// <summary>
    /// Cancelling twice produces one outcome and no
    /// <see cref="ObjectDisposedException"/>. A user stopping a run they already
    /// stopped is ordinary, and the second stop must be inert.
    /// </summary>
    [Fact]
    public async Task DoubleCancel_ProducesOneOutcomeAndNoDisposedException()
    {
        var gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var run = RunAsync(new BlockingTransport(gate, emitFirst: true), cts.Token);
        await gate.Task;
        await cts.CancelAsync();
        await cts.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => run);

        // One outcome, and specifically not a disposal race surfacing as a
        // second, different failure.
        Assert.Null(thrown);
        Assert.Equal(AgentLoopOutcome.Cancelled, (await run).Outcome);
    }
}
