using VisualRelay.Core.Llm;
using static VisualRelay.Tests.CassetteTestHelpers;

namespace VisualRelay.Tests;

/// <summary>
/// The replayer: a hit serves the recorded exchange byte-for-byte, and a miss
/// throws a message that names the file it wanted and the field that moved.
/// Nothing here can reach a socket — the type holds no transport to fall back to.
/// </summary>
public sealed partial class ReplayTransportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vr-cassettes", Guid.NewGuid().ToString("N"));

    /// <summary>Removes the temporary cassette tree this test wrote.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A recorded buffered exchange replays with its status, headers and body.</summary>
    [Fact]
    public async Task SendAsync_Hit_ReturnsTheRecordedResponse()
    {
        var request = Post(ChatBody("hi"));
        var store = await RecordBufferedAsync(request);

        var response = await new ReplayTransport(store, "deepseek", "chat").SendAsync(request);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", response.Headers["content-type"]);
        Assert.Equal("""{"choices":[{"message":{"content":"hey"}}]}""", response.Body);
    }

    /// <summary>A recorded stream replays chunk for chunk, in the order it arrived.</summary>
    [Fact]
    public async Task StreamAsync_Hit_ReplaysTheChunksInOrder()
    {
        var request = Post(ChatBody("hi"));
        byte[][] chunks =
        [
            "data: {\"delta\":\"he\"}\n\n"u8.ToArray(),
            "data: {\"delta\":\"llo\"}\n\n"u8.ToArray(),
            "data: [DONE]\n\n"u8.ToArray(),
        ];
        var store = await RecordStreamAsync(request, chunks);

        var received = new List<byte[]>();
        await using (var stream = await new ReplayTransport(store, "zai", "stream").StreamAsync(request))
        {
            Assert.Equal(200, stream.StatusCode);
            Assert.Equal("text/event-stream", stream.Headers["content-type"]);
            await foreach (var chunk in stream.ReadChunksAsync())
                received.Add(chunk.ToArray());
        }

        Assert.Equal(chunks, received);
    }

    /// <summary>
    /// A miss names the file it looked for and diffs the request against the
    /// nearest recorded one, so the changed field is in the failure message rather
    /// than in whatever the reader can reconstruct from two SHA-256s.
    /// </summary>
    [Fact]
    public async Task SendAsync_Miss_NamesTheDifferingFieldAgainstTheNearestCassette()
    {
        var store = await RecordBufferedAsync(Post(ChatBody("hi")));
        var changed = Post(ChatBody("hello"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReplayTransport(store, "deepseek", "chat").SendAsync(changed));

        Assert.Contains("cassette miss", error.Message, StringComparison.Ordinal);
        Assert.Contains("1 differing field", error.Message, StringComparison.Ordinal);
        Assert.Contains("body.messages[0].content", error.Message, StringComparison.Ordinal);
        Assert.Contains("cassette: \"hi\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("request : \"hello\"", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            store.PathFor("deepseek", "chat", CassetteKey.Compute(changed)),
            error.Message, StringComparison.Ordinal);
    }

    /// <summary>Records one buffered chat exchange and hands back the store holding it.</summary>
    private async Task<CassetteStore> RecordBufferedAsync(ProviderRequest request)
    {
        var store = new CassetteStore(_root);
        var inner = new StubProviderTransport(
            200,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "application/json" },
            """{"choices":[{"message":{"content":"hey"}}]}""");

        await new RecordingTransport(inner, store, "deepseek", "chat").SendAsync(request);
        return store;
    }

    /// <summary>Records one streamed exchange and hands back the store holding it.</summary>
    private async Task<CassetteStore> RecordStreamAsync(ProviderRequest request, byte[][] chunks)
    {
        var store = new CassetteStore(_root);
        var inner = new StubProviderTransport(
            200,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "text/event-stream" },
            body: "",
            chunks);

        await using var stream = await new RecordingTransport(inner, store, "zai", "stream")
            .StreamAsync(request);
        await foreach (var chunk in stream.ReadChunksAsync())
            Assert.False(chunk.IsEmpty);

        return store;
    }
}
