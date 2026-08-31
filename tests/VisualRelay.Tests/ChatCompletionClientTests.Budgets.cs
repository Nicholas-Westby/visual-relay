using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// The budget half of <see cref="ChatCompletionClientTests"/>: the three stream
/// clocks, the stalls each one catches, and the long healthy stream that none of
/// them may kill.
/// </summary>
public sealed partial class ChatCompletionClientTests
{
    /// <summary>
    /// The case that must NOT fire: a stream that trickles a chunk every thirty
    /// seconds for fifty virtual minutes stays alive, because every gap is inside
    /// the idle budget even though the run is long. Length alone never kills.
    /// </summary>
    [Fact]
    public async Task SlowButHealthyStream_IsNotKilled()
    {
        // Twenty-five two-minute gaps is fifty virtual minutes. Virtual time is
        // free; the real cost is per step, and this suite has a 60 s ceiling.
        var steps = new List<(TimeSpan, string?)>();
        for (var i = 0; i < 25; i++) steps.Add((TimeSpan.FromMinutes(2), Delta("x")));
        steps.Add((TimeSpan.FromMinutes(2), Stop));
        steps.Add((Zero, Done));

        // Time to first byte is raised to match the idle budget so the first gap
        // is judged by the same rule as every later one; this test is about
        // duration, not about the first-byte case.
        var completion = await RunAsync(steps, timeouts: Budgets(firstByte: 300, idle: 300));

        Assert.Equal(CompletionOutcome.Completed, completion.Outcome);
        Assert.Equal(25, completion.Content.Length);
    }

    /// <summary>
    /// A provider that accepts the request and never writes a byte fails on the
    /// time-to-first-byte budget, not on the whole-request ceiling.
    /// </summary>
    [Fact]
    public async Task AcceptThenNeverWrite_TimesOutOnFirstByte()
    {
        var completion = await RunAsync([(TimeSpan.FromSeconds(31), null)], complete: false);

        Assert.Equal(CompletionOutcome.TimedOut, completion.Outcome);
        Assert.Equal(string.Empty, completion.Content);
    }

    /// <summary>
    /// A stream that starts healthily then stalls fails on the inter-chunk idle
    /// budget, and whatever content already arrived is preserved.
    /// </summary>
    [Fact]
    public async Task MidStreamStall_TimesOutAndKeepsPartialContent()
    {
        var completion = await RunAsync(
            [(Zero, Delta("partial")), (TimeSpan.FromSeconds(46), null)], complete: false);

        Assert.Equal(CompletionOutcome.TimedOut, completion.Outcome);
        Assert.Equal("partial", completion.Content);
    }

    /// <summary>
    /// Keepalive comments hold the connection open across gaps that would
    /// otherwise expire the idle budget, and produce no output of their own.
    /// </summary>
    [Fact]
    public async Task Keepalives_HoldTheConnectionWithoutProducingOutput()
    {
        var seen = new List<string>();
        var steps = new List<(TimeSpan, string?)>();
        for (var i = 0; i < 6; i++) steps.Add((TimeSpan.FromSeconds(40), ": keepalive\n"));
        steps.Add((TimeSpan.FromSeconds(40), Delta("finally")));
        steps.Add((Zero, Stop));
        steps.Add((Zero, Done));

        // Seven 40 s gaps is 280 s of stream against a 60 s idle budget: without
        // the keepalives resetting that clock this would have been killed.
        var completion = await RunAsync(
            steps, onOutput: seen.Add, timeouts: Budgets(firstByte: 60, idle: 60));

        Assert.Equal(CompletionOutcome.Completed, completion.Outcome);
        Assert.Equal("finally", completion.Content);
        Assert.Equal(["finally"], seen);
    }
}
