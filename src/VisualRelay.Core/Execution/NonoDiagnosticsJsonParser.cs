using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Tolerant parser that finds the last balanced <c>{…}</c> block containing
/// <c>"denials"</c> in a captured output string, extracts the denial records,
/// and strips the block from the output.  Absent, truncated, or malformed JSON
/// never throws — the method returns <c>false</c> with the output unchanged.
/// </summary>
internal static class NonoDiagnosticsJsonParser
{
    public static bool TryExtractDenials(
        string? output,
        out string stripped,
        out List<SandboxDenial> denials)
    {
        denials = new List<SandboxDenial>();

        if (string.IsNullOrEmpty(output))
        {
            stripped = output!; // preserve null for null input
            return false;
        }

        // Find the LAST balanced {…} block that contains "denials".
        // Scan backwards from the end.
        var span = output.AsSpan();
        var lastOpen = -1;
        for (var i = span.Length - 1; i >= 0; i--)
        {
            if (span[i] == '{')
            {
                // Try to find the matching close brace from this position.
                var depth = 1;
                var close = -1;
                for (var j = i + 1; j < span.Length; j++)
                {
                    if (span[j] == '{') depth++;
                    else if (span[j] == '}') depth--;
                    if (depth == 0) { close = j; break; }
                }

                if (close >= 0)
                {
                    // Check if this block contains "denials".
                    var block = span.Slice(i, close - i + 1);
                    if (block.ToString().Contains("\"denials\"", StringComparison.Ordinal))
                    {
                        lastOpen = i;
                        // Extract denials from this block.
                        if (TryParseDenials(block, denials))
                        {
                            // Strip the JSON block from the output.
                            stripped = output[..i] + output[(close + 1)..];
                            return true;
                        }
                        // If parsing failed, fall through - don't strip.
                        stripped = output;
                        return false;
                    }
                }
            }
        }

        stripped = output;
        return false;
    }

    private static bool TryParseDenials(ReadOnlySpan<char> jsonBlock, List<SandboxDenial> denials)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBlock.ToString());
            if (!doc.RootElement.TryGetProperty("denials", out var denialsElement))
                return false;

            if (denialsElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in denialsElement.EnumerateArray())
            {
                string? operation = null;
                string? target = null;

                if (item.TryGetProperty("operation", out var opProp) &&
                    opProp.ValueKind == JsonValueKind.String)
                    operation = opProp.GetString();

                if (item.TryGetProperty("target", out var tgtProp) &&
                    tgtProp.ValueKind == JsonValueKind.String)
                    target = tgtProp.GetString();

                if (operation is not null && target is not null)
                    denials.Add(new SandboxDenial(operation, target));
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
