using System.Text.Json.Nodes;

namespace VisualRelay.Tests;

/// <summary>
/// One differing JSON path between a recorded request and the live one.
/// </summary>
/// <param name="Path">The JSON path, e.g. <c>body.messages[0].content</c>.</param>
/// <param name="Cassette">The recorded value, rendered as JSON.</param>
/// <param name="Request">The live value, rendered as JSON.</param>
internal sealed record CassetteDifference(string Path, string Cassette, string Request);

/// <summary>
/// A structural diff between two canonical requests. A cassette miss is almost
/// always one changed field — a bumped temperature, an extra tool, a reworded
/// system prompt — and printing the SHA-256 of each side says nothing about
/// which. This walks both documents together and names the paths that differ,
/// so the miss message points at the field instead of at a hash.
/// </summary>
internal static class CassetteDiff
{
    private const int MaxValueLength = 120;

    /// <summary>Every path at which the two documents differ, in document order.</summary>
    /// <param name="cassette">The recorded canonical request.</param>
    /// <param name="request">The live canonical request.</param>
    /// <returns>The differences; empty when the two are structurally equal.</returns>
    public static IReadOnlyList<CassetteDifference> Compare(JsonNode? cassette, JsonNode? request)
    {
        var differences = new List<CassetteDifference>();
        Walk("", cassette, request, differences);
        return differences;
    }

    private static void Walk(string path, JsonNode? cassette, JsonNode? request, List<CassetteDifference> into)
    {
        if (cassette is JsonObject left && request is JsonObject right)
        {
            var names = left.Select(entry => entry.Key)
                .Union(right.Select(entry => entry.Key), StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal);
            foreach (var name in names)
                Walk(Join(path, name), left[name], right[name], into);
            return;
        }

        if (cassette is JsonArray leftItems && request is JsonArray rightItems)
        {
            for (var index = 0; index < Math.Max(leftItems.Count, rightItems.Count); index++)
                Walk(
                    $"{path}[{index}]",
                    index < leftItems.Count ? leftItems[index] : null,
                    index < rightItems.Count ? rightItems[index] : null,
                    into);
            return;
        }

        var recorded = Render(cassette);
        var live = Render(request);
        if (!string.Equals(recorded, live, StringComparison.Ordinal))
            into.Add(new CassetteDifference(path.Length == 0 ? "(whole request)" : path, recorded, live));
    }

    private static string Join(string path, string name) =>
        path.Length == 0 ? name : $"{path}.{name}";

    /// <summary>
    /// Renders one node for the message. A missing property and an explicit JSON
    /// null render alike — the node model does not distinguish them, and for a
    /// cassette key they mean the same thing.
    /// </summary>
    private static string Render(JsonNode? node)
    {
        if (node is null) return "(absent)";

        var json = node.ToJsonString();
        return json.Length <= MaxValueLength
            ? json
            : json[..MaxValueLength] + $"... ({json.Length} chars)";
    }
}
