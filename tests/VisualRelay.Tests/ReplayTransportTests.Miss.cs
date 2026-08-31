using static VisualRelay.Tests.CassetteTestHelpers;

namespace VisualRelay.Tests;

/// <summary>
/// The rest of the miss contract: replay refuses to improvise. It never falls
/// through to the network, it says so when a scenario has nothing recorded, and
/// it will not serve a streamed cassette down the buffered path or the reverse.
/// </summary>
public sealed partial class ReplayTransportTests
{
    /// <summary>
    /// The streaming path misses just as loudly, and the message says outright
    /// that no network call was made — the reader should never wonder whether a
    /// half-real request went out.
    /// </summary>
    [Fact]
    public async Task StreamAsync_Miss_ThrowsAndSaysItNeverReachesTheNetwork()
    {
        var store = await RecordBufferedAsync(Post(ChatBody("hi")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReplayTransport(store, "deepseek", "chat").StreamAsync(Post(ChatBody("hello"))));

        Assert.Contains("NEVER falls through to the network", error.Message, StringComparison.Ordinal);
        Assert.Contains("operation: StreamAsync", error.Message, StringComparison.Ordinal);
        Assert.Contains("re-record this exchange with RecordingTransport", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An empty scenario says so instead of pointing at a nearest cassette that does not exist.</summary>
    [Fact]
    public async Task SendAsync_Miss_WithNothingRecorded_SaysTheScenarioIsEmpty()
    {
        var replay = new ReplayTransport(new CassetteStore(_root), "deepseek", "chat");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => replay.SendAsync(Post(ChatBody("hi"))));

        Assert.Contains("no cassettes recorded under deepseek/chat yet", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A streamed cassette is not served down the buffered path: concatenating
    /// chunks into a body would quietly change what the caller sees.
    /// </summary>
    [Fact]
    public async Task SendAsync_AgainstAStreamedCassette_RefusesInsteadOfImprovising()
    {
        var request = Post(ChatBody("hi"));
        var store = await RecordStreamAsync(request, ["data: [DONE]\n\n"u8.ToArray()]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReplayTransport(store, "zai", "stream").SendAsync(request));

        Assert.Contains("records a STREAMING exchange", error.Message, StringComparison.Ordinal);
    }

    /// <summary>And the reverse: a buffered cassette is not served down the streaming path.</summary>
    [Fact]
    public async Task StreamAsync_AgainstABufferedCassette_RefusesInsteadOfImprovising()
    {
        var request = Post(ChatBody("hi"));
        var store = await RecordBufferedAsync(request);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReplayTransport(store, "deepseek", "chat").StreamAsync(request));

        Assert.Contains("records a BUFFERED exchange", error.Message, StringComparison.Ordinal);
    }
}
