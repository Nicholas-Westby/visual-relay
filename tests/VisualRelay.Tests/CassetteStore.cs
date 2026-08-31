using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Tests;

/// <summary>
/// Reads and writes cassette files under
/// <c>&lt;root&gt;/&lt;provider&gt;/&lt;scenario&gt;/&lt;key&gt;.json</c> — one JSON
/// file per exchange, so a re-record is a readable per-exchange diff rather than
/// one churning mega-file, and a cassette can be deleted on its own.
/// <para>
/// The root is injected rather than resolved from the repo so a test can record
/// into a temp directory without touching the committed tree; the committed tree
/// is <c>tests/VisualRelay.Tests/Cassettes</c>, which the csproj copies to the
/// output directory and <c>.gitattributes</c> marks <c>-diff</c>.
/// </para>
/// </summary>
/// <param name="root">The directory the provider folders live under.</param>
internal sealed class CassetteStore(string root)
{
    // Indented and relaxed-escaped: a cassette is read by people as often as by
    // the replayer, and the default encoder turns every apostrophe and angle
    // bracket in a prompt into a \uXXXX escape. The escaping is a serialization
    // detail only — the key is hashed from the canonical form, never from the
    // file text, so it cannot move a cassette out from under a replay.
    private static readonly JsonSerializerOptions Indented =
        new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The file one exchange is filed at, whether or not it exists.</summary>
    /// <param name="provider">The provider directory name.</param>
    /// <param name="scenario">The scenario directory name.</param>
    /// <param name="key">The SHA-256 hex key.</param>
    /// <returns>The absolute cassette path.</returns>
    public string PathFor(string provider, string scenario, string key) =>
        Path.Combine(DirectoryFor(provider, scenario), key + ".json");

    /// <summary>Writes one exchange, creating the provider/scenario directories.</summary>
    /// <param name="record">The record to persist.</param>
    public void Write(CassetteRecord record)
    {
        Directory.CreateDirectory(DirectoryFor(record.Provider, record.Scenario));
        var path = PathFor(record.Provider, record.Scenario, record.Key);
        File.WriteAllText(path, record.ToJson().ToJsonString(Indented) + "\n");
    }

    /// <summary>Reads one exchange, or null when no cassette is filed under that key.</summary>
    /// <param name="provider">The provider directory name.</param>
    /// <param name="scenario">The scenario directory name.</param>
    /// <param name="key">The SHA-256 hex key.</param>
    /// <returns>The record, or null on a miss.</returns>
    public CassetteRecord? TryRead(string provider, string scenario, string key)
    {
        var path = PathFor(provider, scenario, key);
        return File.Exists(path) ? Read(path) : null;
    }

    /// <summary>
    /// Every cassette in one scenario, ordered by file name — the candidate set a
    /// miss is diffed against to find the nearest recorded request.
    /// </summary>
    /// <param name="provider">The provider directory name.</param>
    /// <param name="scenario">The scenario directory name.</param>
    /// <returns>The scenario's records; empty when the directory does not exist.</returns>
    public IReadOnlyList<CassetteRecord> ReadScenario(string provider, string scenario)
    {
        var directory = DirectoryFor(provider, scenario);
        if (!Directory.Exists(directory)) return [];

        return
        [
            .. Directory.EnumerateFiles(directory, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(Read),
        ];
    }

    private string DirectoryFor(string provider, string scenario) =>
        Path.Combine(root, provider, scenario);

    private static CassetteRecord Read(string path) =>
        CassetteRecord.FromJson(
            JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException($"Cassette file is not a JSON object: {path}"));
}
