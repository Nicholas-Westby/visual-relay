using static VisualRelay.Tests.CassetteTestHelpers;

namespace VisualRelay.Tests;

/// <summary>
/// The cassette file format and its layout on disk: one readable JSON file per
/// exchange under &lt;provider&gt;/&lt;scenario&gt;/&lt;key&gt;.json, round-tripping
/// buffered bodies and ordered stream chunks, plus the build wiring that ships
/// the tree.
/// </summary>
public sealed class CassetteStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vr-cassettes", Guid.NewGuid().ToString("N"));

    /// <summary>Removes the temporary cassette tree this test wrote.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// A buffered exchange round-trips, is filed under the key a replay computes
    /// from the same request, and stays readable: the file names the canonicalizer
    /// version and carries the request a human needs to recognize it.
    /// </summary>
    [Fact]
    public void Write_ThenTryRead_RoundTripsABufferedExchange()
    {
        var store = new CassetteStore(_root);
        var request = Post(ChatBody("hi"), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accept"] = "application/json",
        });
        var record = CassetteRecord.Create(
            "deepseek", "chat", request, 200,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "application/json" },
            """{"choices":[]}""", chunks: null);

        store.Write(record);

        var path = store.PathFor("deepseek", "chat", record.Key);
        Assert.Equal(Path.Combine(_root, "deepseek", "chat", record.Key + ".json"), path);
        Assert.Equal(CassetteKey.Compute(request), record.Key);

        var text = File.ReadAllText(path);
        Assert.Contains("\"canonicalizerVersion\": 1", text, StringComparison.Ordinal);
        Assert.Contains("test-model", text, StringComparison.Ordinal);
        Assert.Contains("/v1/chat/completions", text, StringComparison.Ordinal);

        var read = store.TryRead("deepseek", "chat", record.Key)!;
        Assert.Equal(CassetteKey.Version, read.Version);
        Assert.Equal(200, read.StatusCode);
        Assert.Equal("application/json", read.ResponseHeaders["content-type"]);
        Assert.Equal("""{"choices":[]}""", read.Body);
        Assert.Null(read.Chunks);
        Assert.Equal(CassetteKey.Canonical(request).ToJsonString(), read.Request.ToJsonString());
    }

    /// <summary>
    /// Stream chunks round-trip byte-for-byte and in order. They are base64 in the
    /// file — a provider may split an SSE event mid-codepoint, so a text array
    /// would not survive the trip.
    /// </summary>
    [Fact]
    public void Write_ThenTryRead_RoundTripsStreamChunksInOrder()
    {
        var store = new CassetteStore(_root);
        byte[][] chunks =
        [
            "data: {\"delta\":\"he\"}\n\n"u8.ToArray(),
            "data: {\"delta\":\"llo\"}\n\n"u8.ToArray(),
            "data: [DONE]\n\n"u8.ToArray(),
        ];
        var record = CassetteRecord.Create(
            "zai", "stream", Post(ChatBody("hi")), 200,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "text/event-stream" },
            body: "", chunks);

        store.Write(record);

        var text = File.ReadAllText(store.PathFor("zai", "stream", record.Key));
        Assert.Contains("\"chunkEncoding\": \"base64\"", text, StringComparison.Ordinal);

        var read = store.TryRead("zai", "stream", record.Key)!;
        Assert.Equal(chunks.Length, read.Chunks!.Count);
        Assert.Equal(chunks, read.Chunks);
        Assert.Equal("", read.Body);
    }

    /// <summary>An unrecorded key reads back as null, and an unrecorded scenario as empty.</summary>
    [Fact]
    public void TryRead_UnknownKey_IsAMissAndReadsNoScenario()
    {
        var store = new CassetteStore(_root);

        Assert.Null(store.TryRead("deepseek", "chat", new string('0', 64)));
        Assert.Empty(store.ReadScenario("deepseek", "chat"));
    }

    /// <summary>Every exchange in a scenario is its own file, and all of them are readable back.</summary>
    [Fact]
    public void ReadScenario_ReturnsEveryCassetteInTheScenario()
    {
        var store = new CassetteStore(_root);
        foreach (var content in new[] { "one", "two", "three" })
            store.Write(CassetteRecord.Create(
                "deepseek", "chat", Post(ChatBody(content)), 200,
                new Dictionary<string, string>(StringComparer.Ordinal), "{}", chunks: null));

        var records = store.ReadScenario("deepseek", "chat");

        Assert.Equal(3, records.Count);
        Assert.Equal(3, records.Select(record => record.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(records, record => Assert.Equal("chat", record.Scenario));
    }

    /// <summary>The csproj copies the committed cassette tree to the test output, beside Fixtures.</summary>
    [Fact]
    public void Csproj_CopiesTheCassetteTreeToTheOutputDirectory()
    {
        var csproj = File.ReadAllText(Path.Combine(
            RepoSetup.Root, "tests", "VisualRelay.Tests", "VisualRelay.Tests.csproj"));

        Assert.Contains(
            """<None Include="Cassettes\**\*" CopyToOutputDirectory="PreserveNewest" />""",
            csproj, StringComparison.Ordinal);
    }

    /// <summary>Cassettes are marked -diff: a re-record is a replaced file, not a JSON hunk.</summary>
    [Fact]
    public void GitAttributes_MarksTheCassetteTreeNoDiff()
    {
        var attributes = File.ReadAllText(Path.Combine(RepoSetup.Root, ".gitattributes"));

        Assert.Contains("tests/VisualRelay.Tests/Cassettes/** -diff", attributes, StringComparison.Ordinal);
    }
}
