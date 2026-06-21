using System.Text.RegularExpressions;

namespace VisualRelay.Core.CommitLint;

/// <summary>
/// Contextual checks that scan the subject and each bullet for a path-like
/// token or a changed-file basename. Only basenames that contain <c>.</c> or are
/// at least six characters long are matched (a bare <c>app</c> is too generic),
/// using word boundaries — faithful to ai-sorcery's reference. Skipped entirely
/// for the driver tier.
/// </summary>
internal static class ConceptCheck
{
    public static void Check(
        string subject,
        IReadOnlyList<string> bullets,
        CommitLintContext context,
        List<Violation> violations)
    {
        var basenamePatterns = BuildBasenameMatchers(context.ChangedBasenames);
        CheckText(subject, "subject", basenamePatterns, violations);
        foreach (var bullet in bullets)
            CheckText(bullet, "bullet", basenamePatterns, violations);
    }

    private static void CheckText(
        string text,
        string where,
        IReadOnlyList<(string Name, Regex Pattern)> basenames,
        List<Violation> violations)
    {
        if (CommitRules.PathToken.IsMatch(text))
        {
            violations.Add(new Violation(
                $"{where} must not contain a path-like token (a '/' with non-space on both sides): \"{text}\""));
        }

        foreach (var (name, pattern) in basenames)
        {
            if (pattern.IsMatch(text))
            {
                violations.Add(new Violation(
                    $"{where} must not name a changed file ('{name}'); describe the change by behavior: \"{text}\""));
            }
        }
    }

    private static IReadOnlyList<(string, Regex)> BuildBasenameMatchers(IReadOnlyList<string> changedBasenames)
    {
        var matchers = new List<(string, Regex)>();
        foreach (var basename in changedBasenames.Distinct(StringComparer.Ordinal))
        {
            // Only basenames that contain '.' OR are >= 6 chars are matched —
            // short, dotless names are too generic to flag reliably.
            if (basename.Length == 0)
                continue;
            if (!basename.Contains('.', StringComparison.Ordinal) && basename.Length < 6)
                continue;

            matchers.Add((basename, new Regex($@"\b{Regex.Escape(basename)}\b", RegexOptions.None)));
        }

        return matchers;
    }
}
