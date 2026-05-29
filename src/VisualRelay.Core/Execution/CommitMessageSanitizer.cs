namespace VisualRelay.Core.Execution;

internal static class CommitMessageSanitizer
{
    private const int MaxSubjectLength = 72;
    private static readonly string[] Types = ["feat", "fix", "docs", "style", "refactor", "perf", "test", "build", "ci", "chore", "revert"];

    public static string FromRawOrFallback(string? raw, string taskId)
    {
        var fallback = Truncate($"chore(relay): {taskId}");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var lines = raw.Trim().Split('\n');
        var subject = SanitizeSubject(lines[0]);
        if (!HasConventionalPrefix(subject))
        {
            return fallback;
        }

        var bullets = lines
            .Skip(1)
            .Select(line => line.Replace("\u2014", "-", StringComparison.Ordinal).TrimEnd())
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal) && line.Length > 2)
            .Take(3)
            .ToArray();
        return bullets.Length == 0
            ? subject
            : $"{subject}{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, bullets)}";
    }

    private static string SanitizeSubject(string subject)
    {
        var clean = subject.Replace("\u2014", "-", StringComparison.Ordinal).TrimEnd();
        if (clean.EndsWith(".", StringComparison.Ordinal))
        {
            clean = clean[..^1];
        }

        return Truncate(clean);
    }

    private static string Truncate(string value)
    {
        if (value.Length <= MaxSubjectLength)
        {
            return value;
        }

        var truncated = value[..MaxSubjectLength];
        var lastSpace = truncated.LastIndexOf(' ');
        return (lastSpace > 0 ? truncated[..lastSpace] : truncated).TrimEnd();
    }

    private static bool HasConventionalPrefix(string subject) =>
        Types.Any(type =>
            subject.StartsWith($"{type}: ", StringComparison.Ordinal) ||
            subject.StartsWith($"{type}(", StringComparison.Ordinal));
}
