using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Covers <see cref="ChatCompletionClient"/> end to end on virtual time: the
/// three budgets, the outcomes the driver branches on, and the case that must
/// NOT fire — a slow but healthy stream running for the best part of an hour.
/// </summary>
public sealed partial class ChatCompletionClientTests
{
    private static readonly ProviderRequest Request = new(
        HttpMethod.Post, new Uri("https://provider.test/v1/chat/completions"),
        new Dictionary<string, string>(), "{}");

    private static string Delta(string text) =>
        $$"""data: {"choices":[{"delta":{"content":"{{text}}"},"finish_reason":null}]}""" + "\n\n";

    private const string Stop =
        """data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}""" + "\n\n";

    private const string Done = "data: [DONE]\n\n";

    private static readonly TimeSpan Zero = TimeSpan.Zero;

    /// <summary>
    /// Runs one exchange. Each step advances the clock by its gap and then, when
    /// it carries text, hands that chunk over. Advancing before delivering is
    /// what makes the gap real to the client's budget while keeping delivery
    /// itself off the clock, so the ordering is fixed run to run.
    /// A step with null text is a gap that never ends: the stall cases.
    /// </summary>
    private static async Task<ChatCompletion> RunAsync(
        IReadOnlyList<(TimeSpan Gap, string? Text)> steps,
        int statusCode = 200,
        Action<string>? onOutput = null,
        ProviderTimeouts? timeouts = null,
        bool complete = true)
    {
        var clock = new ManualTimeProvider();
        var transport = new ControlledStreamTransport(statusCode);
        var client = new ChatCompletionClient(transport, timeouts, clock);

        var run = client.StreamAsync(Request, onOutput);
        await DrainAsync();

        foreach (var (gap, text) in steps)
        {
            if (gap > TimeSpan.Zero)
            {
                clock.Advance(gap);
                await DrainAsync();
            }

            if (text is null) break;
            transport.Emit(text);
            await DrainAsync();
            if (run.IsCompleted) break;
        }

        if (complete) transport.Complete();
        await DrainAsync();

        // Let any expired budget resolve.
        for (var i = 0; i < 120 && !run.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await DrainAsync();
        }

        return await run;
    }

    /// <summary>Budgets with the two stream clocks set explicitly.</summary>
    /// <param name="firstByte">Time-to-first-byte budget, in seconds.</param>
    /// <param name="idle">Inter-chunk idle budget, in seconds.</param>
    /// <returns>The budget set.</returns>
    private static ProviderTimeouts Budgets(int firstByte, int idle) => new(
        Connect: TimeSpan.FromSeconds(10),
        TimeToFirstByte: TimeSpan.FromSeconds(firstByte),
        InterChunkIdle: TimeSpan.FromSeconds(idle),
        Total: TimeSpan.FromHours(2));

    /// <summary>
    /// Lets queued continuations run before the clock moves again.
    /// <para>
    /// This needs a real scheduling window, not <c>Task.Yield</c>. Yielding
    /// returns to xUnit's synchronization context, while the continuations that
    /// matter here — a channel read completing and a <c>Task.WhenAny</c>
    /// resolving — are posted to the thread pool. Measured directly: after a
    /// chunk was written, sixteen yields never observed it and a real delay did.
    /// Only the scheduling is real; every budget under test is still virtual.
    /// </para>
    /// </summary>
    private static async Task DrainAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(1); // vr-allow-sleep: real scheduling window for pool continuations
        }
    }

    /// <summary>A clean stream completes with content, usage and a finish reason.</summary>
    [Fact]
    public async Task CleanStream_CompletesWithMeasuredUsage()
    {
        var completion = await RunAsync(
            [(Zero, Delta("Hello")), (Zero, Stop), (Zero, Done)]);

        Assert.Equal(CompletionOutcome.Completed, completion.Outcome);
        Assert.Equal("Hello", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(5, completion.Usage!.PromptTokens);
    }

    /// <summary>Content deltas reach the caller as they arrive, not at the end.</summary>
    [Fact]
    public async Task ContentDeltas_AreStreamedToTheCaller()
    {
        var seen = new List<string>();

        await RunAsync(
            [(Zero, Delta("one")), (Zero, Delta("two")), (Zero, Stop), (Zero, Done)],
            onOutput: seen.Add);

        Assert.Equal(["one", "two"], seen);
    }

    /// <summary>
    /// A stream ending without its terminator is truncated, not complete, even
    /// though every frame it did send parsed cleanly.
    /// </summary>
    [Fact]
    public async Task StreamWithoutDone_IsTruncated()
    {
        var completion = await RunAsync([(Zero, Delta("cut")), (Zero, Stop)]);

        Assert.Equal(CompletionOutcome.Truncated, completion.Outcome);
        Assert.Equal("cut", completion.Content);
    }

    /// <summary>A non-2xx status fails with the provider's own error text.</summary>
    [Fact]
    public async Task ErrorStatus_FailsWithTheProviderError()
    {
        var completion = await RunAsync(
            [(Zero, """{"error":{"message":"Too many requests"}}""")], statusCode: 429);

        Assert.Equal(CompletionOutcome.Failed, completion.Outcome);
        Assert.Equal(ProviderErrorKind.RateLimit, completion.Error!.Kind);
        Assert.True(completion.Error.IsRetryable);
    }

    /// <summary>
    /// A mid-stream error on an already-200 response is caught: that is how
    /// Hugging Face reports a failure it could not report in the status.
    /// </summary>
    [Fact]
    public async Task MidStreamErrorOnA200_IsCaught()
    {
        var completion = await RunAsync(
            [(Zero, Delta("partial")),
             (Zero, """data: {"error":{"message":"upstream exploded"}}""" + "\n\n")]);

        Assert.Equal(CompletionOutcome.Failed, completion.Outcome);
        Assert.Contains("upstream exploded", completion.Error!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The GLM shape gets its own outcome: the budget ran out before any content
    /// was written, which is retryable with a bigger budget rather than an error.
    /// </summary>
    [Fact]
    public async Task LengthFinishWithNoContent_IsItsOwnOutcome()
    {
        var completion = await RunAsync(
            [(Zero, """data: {"choices":[{"delta":{"reasoning_content":"thinking"},"finish_reason":null}]}""" + "\n\n"),
             (Zero, """data: {"choices":[{"delta":{},"finish_reason":"length"}]}""" + "\n\n"),
             (Zero, Done)]);

        Assert.Equal(CompletionOutcome.LengthWithoutContent, completion.Outcome);
        Assert.Equal(string.Empty, completion.Content);
        Assert.Equal("thinking", completion.ReasoningContent);
    }

    /// <summary>
    /// A streamed tool call reaches the caller assembled, alongside the concrete
    /// model the provider reported serving. Cost is attributed to that model, not
    /// to the tier alias, so a fallback hop stays visible.
    /// </summary>
    [Fact]
    public async Task ToolCallsAndServedModel_ReachTheCaller()
    {
        var completion = await RunAsync(
            [(Zero, """data: {"model":"deepseek-v4-flash","choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"add","arguments":"{\"a\":2}"}}]},"finish_reason":null}]}""" + "\n\n"),
             (Zero, """data: {"model":"deepseek-v4-flash","choices":[{"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\n"),
             (Zero, Done)]);

        Assert.Equal(CompletionOutcome.Completed, completion.Outcome);
        Assert.Equal("deepseek-v4-flash", completion.ServedModel);
        var call = Assert.Single(completion.ToolCalls);
        Assert.Equal("add", call.Name);
        Assert.Equal("""{"a":2}""", call.Arguments);
    }

    /// <summary>The request the client sent reaches the transport unchanged.</summary>
    [Fact]
    public async Task Request_IsForwardedToTheTransportVerbatim()
    {
        var clock = new ManualTimeProvider();
        var transport = new ControlledStreamTransport(200);
        var client = new ChatCompletionClient(transport, null, clock);

        var run = client.StreamAsync(Request);
        await DrainAsync();
        transport.Emit(Stop);
        transport.Emit(Done);
        transport.Complete();
        await DrainAsync();
        await run;

        Assert.Equal(Request.Uri, transport.LastRequest!.Uri);
        Assert.Equal(Request.Body, transport.LastRequest.Body);
    }

    /// <summary>A malformed frame is skipped rather than aborting the stream.</summary>
    [Fact]
    public async Task MalformedFrame_IsSkipped()
    {
        var completion = await RunAsync(
            [(Zero, "data: {not json at all\n\n"), (Zero, Delta("ok")),
             (Zero, Stop), (Zero, Done)]);

        Assert.Equal(CompletionOutcome.Completed, completion.Outcome);
        Assert.Equal("ok", completion.Content);
    }
}
