using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.Domain;

namespace VisualRelay.Core.Init;

public static partial class RelayConfigWriter
{
    /// <summary>
    /// Read-modify-write upsert of the <c>authorTests</c> object from a fresh
    /// detection. Detection owns <c>detectedLanguages</c> and
    /// <c>inlineTestExtensions</c>, so a re-bootstrap refreshes both; the
    /// operator owns <c>diffAudit</c> and the top-level <c>testPaths</c> globs, so
    /// those are only seeded when absent and never overwritten. Every other key
    /// is preserved.
    /// </summary>
    /// <param name="rootPath">The repository root.</param>
    /// <param name="detection">What the test-layout detector found.</param>
    public static void UpsertAuthorTests(string rootPath, TestLayoutDetection detection)
    {
        var relayDir = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDir);
        var json = ReadOrCreateConfig(relayDir, out var path);

        if (json["authorTests"] is not JsonObject authorTests)
        {
            authorTests = new JsonObject();
            json["authorTests"] = authorTests;
        }

        authorTests["detectedLanguages"] = ToArray(detection.DetectedLanguages);
        authorTests["inlineTestExtensions"] = ToArray(detection.InlineTestExtensions);
        authorTests["diffAudit"] ??= AuthorTestsConfig.DiffAuditAuto;

        // The repo-specific test globs stay a top-level key: the classifier has
        // read them there since before this object existed, and moving them would
        // silently drop the overrides existing repositories already rely on.
        json["testPaths"] ??= new JsonArray();

        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(JsonValue.Create(value));
        return array;
    }
}
