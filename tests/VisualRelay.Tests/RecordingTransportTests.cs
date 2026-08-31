using static VisualRelay.Tests.CassetteTestHelpers;

namespace VisualRelay.Tests;

/// <summary>
/// The recorder: it must forward the exchange untouched, file it as a cassette,
/// and never let a credential reach the file it writes.
/// </summary>
public sealed class RecordingTransportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vr-cassettes", Guid.NewGuid().ToString("N"));

    /// <summary>Removes the temporary cassette tree this test wrote.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>The buffered response reaches the caller unchanged and lands on disk.</summary>
    [Fact]
    public async Task SendAsync_ForwardsTheExchange_AndFilesACassette()
    {
        var store = new CassetteStore(_root);
        var inner = new StubProviderTransport(
            200,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "application/json" },
            """{"choices":[{"message":{"content":"hey"}}]}""");
        var recorder = new RecordingTransport(inner, store, "deepseek", "chat");
        var request = Post(ChatBody("hi"));

        var response = await recorder.SendAsync(request);

        Assert.Same(request, inner.LastRequest);
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("hey", response.Body, StringComparison.Ordinal);

        var recorded = store.TryRead("deepseek", "chat", CassetteKey.Compute(request))!;
        Assert.Equal(200, recorded.StatusCode);
        Assert.Equal(response.Body, recorded.Body);
        Assert.Equal("application/json", recorded.ResponseHeaders["content-type"]);
    }

    /// <summary>
    /// Redaction on write is the first line of defence, not the secret-scanning
    /// guard: an Authorization request header and a Set-Cookie response header
    /// both fail to reach the file, the latter as an explicit placeholder.
    /// </summary>
    [Fact]
    public async Task SendAsync_NeverPersistsACredential()
    {
        var store = new CassetteStore(_root);
        var inner = new StubProviderTransport(
            200,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["content-type"] = "application/json",
                ["set-cookie"] = "session=abc123; Path=/",
            },
            "{}");
        var recorder = new RecordingTransport(inner, store, "deepseek", "chat");
        var request = Post(ChatBody("hi"), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer sk-live-not-in-the-repo",
            ["accept"] = "application/json",
        });

        await recorder.SendAsync(request);

        var text = File.ReadAllText(store.PathFor("deepseek", "chat", CassetteKey.Compute(request)));
        Assert.DoesNotContain("sk-live-not-in-the-repo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("session=abc123", text, StringComparison.Ordinal);
        Assert.Contains(CassetteHeaders.Redacted, text, StringComparison.Ordinal);
    }

    /// <summary>Chunks reach the caller as they arrive and are recorded in the same order.</summary>
    [Fact]
    public async Task StreamAsync_PassesChunksThrough_AndRecordsThemInOrder()
    {
        var store = new CassetteStore(_root);
        byte[][] chunks =
        [
            "data: {\"delta\":\"he\"}\n\n"u8.ToArray(),
            "data: {\"delta\":\"llo\"}\n\n"u8.ToArray(),
            "data: [DONE]\n\n"u8.ToArray(),
        ];
        var inner = new StubProviderTransport(
            200,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "text/event-stream" },
            body: "",
            chunks);
        var recorder = new RecordingTransport(inner, store, "zai", "stream");
        var request = Post(ChatBody("hi"));

        var received = new List<byte[]>();
        await using (var stream = await recorder.StreamAsync(request))
        {
            Assert.Equal(200, stream.StatusCode);
            await foreach (var chunk in stream.ReadChunksAsync())
                received.Add(chunk.ToArray());
        }

        Assert.Equal(chunks, received);

        var recorded = store.TryRead("zai", "stream", CassetteKey.Compute(request))!;
        Assert.Equal(chunks, recorded.Chunks);
        Assert.Equal("text/event-stream", recorded.ResponseHeaders["content-type"]);
    }

    /// <summary>
    /// A stream the caller walks away from records nothing: a half-drained
    /// exchange would replay as a truncated response, which is worse than a miss.
    /// </summary>
    [Fact]
    public async Task StreamAsync_AbandonedMidStream_WritesNoCassette()
    {
        var store = new CassetteStore(_root);
        var inner = new StubProviderTransport(
            200,
            new Dictionary<string, string>(StringComparer.Ordinal),
            body: "",
            ["first"u8.ToArray(), "second"u8.ToArray()]);
        var recorder = new RecordingTransport(inner, store, "zai", "stream");

        await using (var stream = await recorder.StreamAsync(Post(ChatBody("hi"))))
        {
            await foreach (var chunk in stream.ReadChunksAsync())
            {
                Assert.Equal("first"u8.ToArray(), chunk.ToArray());
                break;
            }
        }

        Assert.Empty(store.ReadScenario("zai", "stream"));
    }
}
