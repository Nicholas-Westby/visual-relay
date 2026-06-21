using VisualRelay.Core.CommitLint;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Normalizes a raw, possibly-messy commit message into one that always passes
/// <see cref="CommitMessageValidator"/>'s <em>structural</em> tier. It consumes
/// the shared <see cref="CommitRules"/> constants so the generator and the hook
/// can never drift. Contextual rules (file names, path tokens) are deliberately
/// NOT applied here — those are relaxed by the hook's driver tier, and scrubbing
/// them would degrade the messages Visual Relay writes into other repos.
/// </summary>
internal static class CommitMessageSanitizer
{
    private const string FallbackPrefix = "chore(relay): ";

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
            .Select(SanitizeBullet)
            .Where(line => line is not null)
            .Take(CommitRules.MaxBullets)
            .ToArray();
        return bullets.Length == 0
            ? subject
            : $"{subject}{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, bullets)}";
    }

    /// <summary>
    /// Returns the sanitized first-line subject if it has a valid Conventional
    /// Commit prefix; otherwise <c>null</c>.  Does not attach bullet points.
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
        var clean = StripTrailingPeriods(subject.Replace(CommitRules.EmDash, '-').TrimEnd());
        clean = LowercaseAfterPrefix(clean);

        // The 72-char word-boundary cut can resurface a trailing period when the
        // surviving word ends in an internal '.', which the validator rejects.
        // Re-strip after truncating. If that empties the description, the result
        // no longer matches the prefix regex, so the caller takes its fallback.
        return StripTrailingPeriods(Truncate(clean));
    }

    /// <summary>Removes any trailing period(s) and trailing whitespace.</summary>
    private static string StripTrailingPeriods(string value) => value.TrimEnd().TrimEnd('.').TrimEnd();

    /// <summary>
    /// Lowercases the first character of the description (immediately after the
    /// <c>type(scope):</c> prefix), closing the validator's
    /// lowercase-after-prefix rule. A no-op when there is no valid prefix.
    /// </summary>
    private static string LowercaseAfterPrefix(string subject)
    {
        var match = CommitRules.FirstCharAfterPrefix.Match(subject);
        if (!match.Success || match.Groups.Count <= 2)
            return subject;

        var charGroup = match.Groups[2];
        var first = charGroup.Value;
        if (first.Length == 1 && char.IsUpper(first[0]))
        {
            var idx = charGroup.Index;
            return subject[..idx] + char.ToLowerInvariant(first[0]) + subject[(idx + 1)..];
        }

        return subject;
    }

    /// <summary>
    /// Normalizes one raw line into a valid bullet, or returns <c>null</c> if it
    /// is not a bullet. Converts em dashes and caps the bullet at
    /// <see cref="CommitRules.MaxBulletWords"/> words on a word boundary so the
    /// validator's word-count rule passes without cutting mid-word.
    /// </summary>
    private static string? SanitizeBullet(string line)
    {
        var clean = line.Replace(CommitRules.EmDash, '-').TrimEnd();
        if (!clean.StartsWith(CommitRules.BulletPrefix, StringComparison.Ordinal)
            || clean.Length <= CommitRules.BulletPrefix.Length)
        {
            return null;
        }

        var text = clean[CommitRules.BulletPrefix.Length..];
        // Split on ALL whitespace to match BodyRules.CheckBulletWords; splitting
        // on a single space would undercount tab-separated words, so the sanitizer
        // would leave an over-cap bullet that the validator then rejects.
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > CommitRules.MaxBulletWords)
        {
            text = string.Join(' ', words.Take(CommitRules.MaxBulletWords));
        }

        return $"{CommitRules.BulletPrefix}{text}";
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
        if (full.Length <= CommitRules.MaxSubjectChars)
        {
            return full;
        }

        // Reserve one char for the ellipsis so the id stays recognizable.
        var idBudget = CommitRules.MaxSubjectChars - FallbackPrefix.Length - 1;
        var head = trimmed[..idBudget].TrimEnd();
        return $"{FallbackPrefix}{head}…";
    }

    private static string Truncate(string value)
    {
        if (value.Length <= CommitRules.MaxSubjectChars)
        {
            return value;
        }

        var truncated = value[..CommitRules.MaxSubjectChars];
        var lastSpace = truncated.LastIndexOf(' ');
        return (lastSpace > 0 ? truncated[..lastSpace] : truncated).TrimEnd();
    }

    /// <summary>
    /// True when <paramref name="subject"/> has a valid structural prefix — a
    /// canonical type, optional <c>[a-z0-9._-]+</c> scope, optional <c>!</c>,
    /// then <c>: </c> and a non-empty description. Uses the shared regex so the
    /// gate matches the validator exactly (accepts <c>feat!: …</c>, rejects a bad
    /// scope like <c>feat(App): …</c>).
    /// </summary>
    private static bool HasConventionalPrefix(string subject) =>
        CommitRules.SubjectPrefix.IsMatch(subject);
}
