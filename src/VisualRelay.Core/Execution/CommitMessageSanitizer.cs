namespace VisualRelay.Core.Execution;

internal static class CommitMessageSanitizer
{
    private const int MaxSubjectLength = 72;
    private const string FallbackPrefix = "chore(relay): ";
    private static readonly string[] Types = ["feat", "fix", "docs", "style", "refactor", "perf", "test", "build", "ci", "chore", "revert"];

    public static string FromRawOrFallback(string? raw, string taskId)
    {
        var fallback = BuildFallbackSubject(taskId);
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

    /// <summary>
    /// Returns the sanitized first-line subject if it has a Conventional Commit
    /// prefix; otherwise <c>null</c>.  Does not attach bullet points.
    /// </summary>
    internal static string? TrySanitizeSubject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var lines = raw.Trim().Split('\n');
        var subject = SanitizeSubject(lines[0]);
        return HasConventionalPrefix(subject) ? subject : null;
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

    /// <summary>
    /// Builds the guaranteed <c>chore(relay): {taskId}</c> fallback subject.  The
    /// <paramref name="taskId"/> is a single hyphenated token with no internal spaces,
    /// so the generic word-boundary <see cref="Truncate"/> would cut at the lone space
    /// after the colon and drop the whole id, yielding an empty <c>chore(relay):</c>.
    /// Instead, when the id overflows the budget we keep a head slice plus an ellipsis
    /// so the description is never empty; the full id still lives in the commit body.
    /// </summary>
    private static string BuildFallbackSubject(string taskId)
    {
        var trimmed = taskId.Trim();
        var full = $"{FallbackPrefix}{trimmed}";
        if (full.Length <= MaxSubjectLength)
        {
            return full;
        }

        // Reserve one char for the ellipsis so the id stays recognizable.
        var idBudget = MaxSubjectLength - FallbackPrefix.Length - 1;
        var head = trimmed[..idBudget].TrimEnd();
        return $"{FallbackPrefix}{head}…";
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
