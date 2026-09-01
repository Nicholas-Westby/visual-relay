using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Covers <c>Retry-After</c>: reading it off a rate-limited response and waiting
/// exactly as long as the provider asked.
/// <para>
/// None of the four providers was ever observed sending this header, which is
/// why the loop's schedule is exponential backoff. That is a measurement, not a
/// guarantee. A provider that starts sending one is stating when its window
/// reopens, and guessing over it is how a rate limit becomes a ban.
/// </para>
/// </summary>
public sealed class ProviderRetryAfterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Delay-seconds is read as that many seconds.</summary>
    /// <param name="header">The header value.</param>
    /// <param name="expectedSeconds">What it should resolve to.</param>
    [Theory]
    [InlineData("30", 30)]
    [InlineData("0", 0)]
    [InlineData("  120  ", 120)]
    public void DelaySeconds_ReadsAsThatManySeconds(string header, int expectedSeconds)
    {
        var wait = ProviderError.ReadRetryAfter(
            new Dictionary<string, string> { ["Retry-After"] = header }, Now);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), wait);
    }

    /// <summary>
    /// The HTTP-date form resolves against the clock, not against wall time, so
    /// it means the same thing under a manual clock as in production.
    /// </summary>
    [Fact]
    public void AnHttpDate_ResolvesAgainstTheClock()
    {
        var wait = ProviderError.ReadRetryAfter(
            new Dictionary<string, string> { ["Retry-After"] = Now.AddSeconds(45).ToString("R") },
            Now);

        Assert.Equal(TimeSpan.FromSeconds(45), wait);
    }

    /// <summary>A date already past means wait no time, not a negative wait.</summary>
    [Fact]
    public void ADateAlreadyPast_MeansNoWait()
    {
        var wait = ProviderError.ReadRetryAfter(
            new Dictionary<string, string> { ["Retry-After"] = Now.AddSeconds(-60).ToString("R") },
            Now);

        Assert.Equal(TimeSpan.Zero, wait);
    }

    /// <summary>The header name is matched without regard to case.</summary>
    [Fact]
    public void TheHeaderName_IsMatchedWithoutRegardToCase()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            ProviderError.ReadRetryAfter(
                new Dictionary<string, string> { ["retry-after"] = "5" }, Now));
    }

    /// <summary>Absent, empty or unparseable means fall back to the schedule.</summary>
    /// <param name="header">A value that carries no usable wait.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("-5")]
    public void AnUnusableValue_FallsBackToTheSchedule(string header)
    {
        Assert.Null(ProviderError.ReadRetryAfter(
            new Dictionary<string, string> { ["Retry-After"] = header }, Now));
    }

    /// <summary>No headers at all is not an error.</summary>
    [Fact]
    public void NoHeaders_IsNotAnError()
    {
        Assert.Null(ProviderError.ReadRetryAfter(null, Now));
        Assert.Null(ProviderError.ReadRetryAfter(new Dictionary<string, string>(), Now));
    }

    /// <summary>
    /// A rate-limited response carries the wait through to the error the loop
    /// reads, rather than being dropped at the client boundary.
    /// </summary>
    [Fact]
    public async Task ARateLimitedResponse_CarriesTheWaitToTheError()
    {
        var transport = new ControlledStreamTransport(
            429, new Dictionary<string, string> { ["Retry-After"] = "17" });
        var clock = new ManualTimeProvider();
        transport.Emit("""{"error":{"message":"slow down","type":"rate_limit_error"}}""");
        transport.Complete();

        var completion = await new ChatCompletionClient(transport, ProviderTimeouts.Default, clock)
            .StreamAsync(new ProviderRequest(
                HttpMethod.Post, new Uri("https://example.test/v1/chat/completions"),
                new Dictionary<string, string>(), "{}"));

        Assert.Equal(CompletionOutcome.Failed, completion.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(17), completion.Error!.RetryAfter);
    }
}
