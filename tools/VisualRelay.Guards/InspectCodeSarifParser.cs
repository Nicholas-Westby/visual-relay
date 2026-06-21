using System.Text.Json;

namespace VisualRelay.Guards;

/// <summary>
/// Pure SARIF result counter — the <c>System.Text.Json</c> replacement for the
/// inline <c>python3</c> in <c>tools/guards/inspect-code.sh</c>. Counts
/// <c>runs[].results[]</c> across all runs. The InspectCode gate fails when the
/// count is non-zero; carve-outs (severity = none) are already removed from the
/// SARIF by <c>.editorconfig</c>, so any remaining result is a real finding.
/// </summary>
public static class InspectCodeSarifParser
{
    /// <summary>
    /// Returns the total number of <c>results</c> entries across every
    /// <c>runs</c> entry in <paramref name="sarifJson"/>. Missing <c>runs</c> or
    /// <c>results</c> arrays contribute zero.
    /// </summary>
    public static int CountResults(string sarifJson)
    {
        using var doc = JsonDocument.Parse(sarifJson);
        if (!doc.RootElement.TryGetProperty("runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var total = 0;
        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array)
            {
                total += results.GetArrayLength();
            }
        }

        return total;
    }
}
