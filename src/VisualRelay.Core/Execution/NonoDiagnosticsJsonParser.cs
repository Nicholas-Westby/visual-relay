using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Tolerant parser that finds the last balanced <c>{…}</c> block containing
/// <c>"denials"</c> in a captured output string, extracts the denial records,
/// and strips the block from the output.  Absent, truncated, or malformed JSON
/// never throws — the method returns <c>false</c> with the output unchanged.
/// <para>
/// The brace walk is STRING-AWARE.  It did not used to be, and a denial whose
/// target held a brace ended the block early: the truncated text failed to
/// parse and every denial for that stage was dropped in silence.  Denials name
/// shell commands, and braces in those are routine.
/// </para>
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

        // One forward pass, tracking string state, then take the LAST block that
        // names denials.  Scanning backwards from the end cannot know whether a
        // brace sits inside a string, which is what made this lossy.
        var blocks = TopLevelBlocks(output);

        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var (blockStart, blockEnd) = blocks[i];
            var block = output.AsSpan(blockStart, blockEnd - blockStart + 1);
            if (!block.Contains("\"denials\"", StringComparison.Ordinal)) continue;

            if (TryParseDenials(block, denials))
            {
                stripped = output[..blockStart] + output[(blockEnd + 1)..];
                return true;
            }

            // This block is the one we wanted and it will not parse.  Do not
            // fall back to an earlier one, and leave the output alone.
            stripped = output;
            return false;
        }

        stripped = output;
        return false;
    }

    /// <summary>
    /// Every balanced top-level <c>{…}</c> span, in order, with braces inside
    /// string literals ignored.
    /// </summary>
    private static List<(int Start, int End)> TopLevelBlocks(string text)
    {
        var blocks = new List<(int, int)>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}' when depth > 0:
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        blocks.Add((start, i));
                        start = -1;
                    }

                    break;
            }
        }

        return blocks;
    }

    private static bool TryParseDenials(ReadOnlySpan<char> jsonBlock, List<SandboxDenial> denials)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBlock.ToString());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            // nono 0.75 nests the record under "session": its "violations" carry the operation and
            // target pairs the older top-level "denials" did, and its "denials" the supervised path
            // refusals. Read the old way, no run since the upgrade reported a denial.
            if (root.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object)
                return TryReadSession(session, denials);

            if (!root.TryGetProperty("denials", out var denialsElement) || denialsElement.ValueKind != JsonValueKind.Array)
                return false;

            AddOperationTargets(denialsElement, denials);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadSession(JsonElement session, List<SandboxDenial> denials)
    {
        if (!session.TryGetProperty("denials", out var supervised) || supervised.ValueKind != JsonValueKind.Array)
            return false;

        if (session.TryGetProperty("violations", out var violations) && violations.ValueKind == JsonValueKind.Array)
            AddOperationTargets(violations, denials);

        // A supervised refusal the violations did not already name (Landlock reports no violations).
        var named = denials.Select(d => d.Target).ToHashSet(StringComparer.Ordinal);
        foreach (var item in supervised.EnumerateArray())
        {
            if (StringProperty(item, "path") is { } path && StringProperty(item, "access") is { } access
                && named.Add(path))
                denials.Add(new SandboxDenial($"file-{access.ToLowerInvariant()}", path));
        }

        return true;
    }

    private static void AddOperationTargets(JsonElement records, List<SandboxDenial> denials)
    {
        foreach (var item in records.EnumerateArray())
        {
            if (StringProperty(item, "operation") is { } operation && StringProperty(item, "target") is { } target)
                denials.Add(new SandboxDenial(operation, target));
        }
    }

    private static string? StringProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
