using System.Text.Json;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Decides whether a balanced Review warrants a second, frontier-tier
/// Review.  Pure helper — side-effect-free, trivially testable.
/// </summary>
internal static class ReviewEscalationPolicy
{
    /// <summary>
    /// Returns true when the balanced Review result warrants a second,
    /// frontier-tier Review.  Escalates on: non-pass verdict, non-empty
    /// issues, or manifest complexity above configured thresholds.
    /// </summary>
    internal static bool ShouldEscalate(
        JsonElement reviewJson,
        IReadOnlyList<string> manifest,
        string rootPath,
        int fileThreshold,
        int lineThreshold)
    {
        // 1. Model signal: non-pass verdict or any issue reported.
        var verdict = reviewJson.TryGetProperty("verdict", out var v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
        if (verdict != "pass") return true;

        if (reviewJson.TryGetProperty("issues", out var issues)
            && issues.ValueKind == JsonValueKind.Array
            && issues.GetArrayLength() > 0) return true;

        // 2. Diff complexity heuristic: file count above threshold.
        if (fileThreshold > 0 && manifest.Count > fileThreshold) return true;

        // 3. Diff complexity heuristic: total lines across manifest files
        //    above threshold.
        if (lineThreshold > 0)
        {
            var totalLines = 0;
            foreach (var rel in manifest)
            {
                // Strip '+' prefix from new-file entries.
                var cleanRel = rel.StartsWith('+') ? rel[1..] : rel;
                var path = Path.Combine(rootPath, cleanRel);
                if (File.Exists(path))
                {
                    totalLines += File.ReadAllLines(path).Length;
                    if (totalLines > lineThreshold) return true;
                }
            }
        }

        return false;
    }
}
