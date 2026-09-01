using VisualRelay.Core.Agent;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// The fault cases the spec's matrix names that the loop was not asserted
/// against: a truncated fenced answer, an empty response, a rate limit with and
/// without <c>Retry-After</c>, and an exact attempt count.
/// <para>
/// All on a manual clock with no network, so a backoff schedule is measured
/// rather than waited out.
/// </para>
/// </summary>
public sealed class AgentFaultMatrixTests
{
    /// <summary>These tests assert on outcomes and requests, not on events.</summary>
    private sealed class SilentSink : IAgentEventSink
    {
        public void Publish(AgentEvent agentEvent) { }
    }

    private static AgentLoopOptions Options(int retryBudget = 2, TimeSpan? backoff = null) =>
        new(
            Model: "fake-1",
            Endpoint: new Uri("https://provider.test/v1/chat/completions"),
            Headers: new Dictionary<string, string>(),
            Capabilities: ProviderCapabilityCatalog.For("DeepSeek"),
            MaxTurns: 6,
            StageBudget: TimeSpan.FromMinutes(30),
            RetryBudget: retryBudget,
            RetryBackoffBase: backoff ?? TimeSpan.Zero);

    private static async Task<AgentLoopResult> RunAsync(
        ScriptedModelTransport transport, AgentLoopOptions options,
        SilentSink sink, TimeProvider? clock = null)
    {
        var provider = clock ?? TimeProvider.System;
        var loop = new AgentTurnLoop(
            new ChatCompletionClient(transport, null, provider), [], sink, provider);
        return await loop.RunAsync(
            [new ChatMessage("user", "go")], options,
            new ToolContext(Path.GetTempPath(), TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// A truncated fenced answer is a completed turn with unusable text: the
    /// loop returns it and the contract reader is what rejects it. The loop must
    /// not invent a retry the model did not ask for.
    /// </summary>
    [Fact]
    public async Task ATruncatedFencedAnswer_ComesBackAsTextTheContractRejects()
    {
        var sink = new SilentSink();
        var transport = new ScriptedModelTransport()
            .AnswersVerbatim("Here is the plan.\n\n```json\n{\"summary\": \"it was go");

        var result = await RunAsync(transport, Options(), sink);

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        var contract = StageContractReader.Read(
            result.Answer, """matching: { "summary": string }""");
        Assert.False(contract.Succeeded);
        Assert.NotNull(contract.Error);
    }

    /// <summary>
    /// A 200 that streams nothing is not a completed turn. The stream ended
    /// without its terminator, so whatever the provider meant, it did not finish.
    /// </summary>
    [Fact]
    public async Task AnEmptyResponse_IsNotACompletedTurn()
    {
        var sink = new SilentSink();
        var transport = new ScriptedModelTransport().AnswersNothing().AnswersNothing().AnswersNothing();

        var result = await RunAsync(transport, Options(), sink);

        Assert.NotEqual(AgentLoopOutcome.Success, result.Outcome);
        Assert.Equal(string.Empty, result.Answer);
    }

    /// <summary>
    /// A rate limit is retried an exact number of times and no more. The budget
    /// is a promise about spend, so an off-by-one is a real cost.
    /// </summary>
    /// <param name="retryBudget">How many retries the loop is allowed.</param>
    /// <param name="expectedRequests">Attempts the provider should see in total.</param>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public async Task ARateLimit_IsRetriedExactlyToTheBudget(int retryBudget, int expectedRequests)
    {
        var sink = new SilentSink();
        var transport = new ScriptedModelTransport();
        for (var i = 0; i < expectedRequests + 2; i++)
            transport.Fails(429, """{"error":{"message":"slow down","type":"rate_limit_error"}}""");

        await RunAsync(transport, Options(retryBudget), sink);

        Assert.Equal(expectedRequests, transport.Requests.Count);
    }

    /// <summary>
    /// Gives pool continuations a real scheduling window. Advancing a manual
    /// clock queues the timer callback on the pool; yielding returns to xUnit's
    /// synchronization context rather than draining it, so a yield-based drain
    /// silently observes nothing. Only the scheduling is real — the ninety
    /// seconds under test stay virtual.
    /// </summary>
    private static async Task DrainAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(1); // vr-allow-sleep: real scheduling window for pool continuations
        }
    }

    /// <summary>
    /// The provider's own <c>Retry-After</c> is waited out to the exact second,
    /// on virtual time. The exponential schedule does not apply on top of it: a
    /// provider that names its window knows something the schedule cannot.
    /// </summary>
    [Fact]
    public async Task RetryAfter_IsWaitedOutExactlyOnVirtualTime()
    {
        var clock = new ManualTimeProvider();
        var sink = new SilentSink();
        var transport = new ScriptedModelTransport()
            .FailsWith(429, """{"error":{"message":"slow down","type":"rate_limit_error"}}""",
                new Dictionary<string, string> { ["Retry-After"] = "90" })
            .Answer("second attempt");

        var started = clock.GetUtcNow();
        var run = RunAsync(transport, Options(retryBudget: 1, backoff: TimeSpan.FromSeconds(1)),
            sink, clock);
        await DrainAsync();

        // One second short of the ask: the loop must still be waiting.
        clock.Advance(TimeSpan.FromSeconds(89));
        await DrainAsync();
        Assert.False(run.IsCompleted, "the loop retried before the provider's window reopened");
        Assert.Single(transport.Requests);

        clock.Advance(TimeSpan.FromSeconds(1));
        await DrainAsync();
        var result = await run;

        Assert.Equal(AgentLoopOutcome.Success, result.Outcome);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(90), clock.GetUtcNow() - started);
    }
}
