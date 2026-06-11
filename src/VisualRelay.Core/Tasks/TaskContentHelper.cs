namespace VisualRelay.Core.Tasks;

internal static class TaskContentHelper
{
    private static readonly HashSet<string> TextExtensions = ["html", "htm", "txt", "json", "csv", "tsv", "xml", "yaml", "yml", "log", "md"];
    private const int PerFileContextLimit = 8_000;
    private const int TotalContextLimit = 24_000;

    public static string StripBatchLine(string markdown)
    {
        if (!markdown.StartsWith("batch:", StringComparison.OrdinalIgnoreCase))
        {
            return markdown;
        }

        var firstNewline = markdown.IndexOf('\n', StringComparison.Ordinal);
        if (firstNewline < 0)
        {
            return string.Empty;
        }

        var rest = markdown[(firstNewline + 1)..];
        return rest.StartsWith('\n') ? rest[1..] : rest;
    }

    public static string? BuildContext(IReadOnlyList<string> siblingPaths)
    {
        if (siblingPaths.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        var used = 0;
        foreach (var path in siblingPaths)
        {
            var name = Path.GetFileName(path);
            var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            var size = new FileInfo(path).Length;
            if (TextExtensions.Contains(extension) && size <= PerFileContextLimit && used < TotalContextLimit)
            {
                var body = File.ReadAllText(path);
                if (used + body.Length > TotalContextLimit)
                {
                    body = string.Concat(body.AsSpan(0, TotalContextLimit - used), "\n...(truncated)");
                }

                used += body.Length;
                parts.Add($"### {name} ({size} bytes){Environment.NewLine}{body}");
            }
            else
            {
                parts.Add($"### {name} ({size} bytes, not inlined)");
            }
        }

        return string.Join($"{Environment.NewLine}{Environment.NewLine}", parts);
    }
}
