namespace VisualRelay.Core.CommitLint;

/// <summary>
/// Structural checks over the body: a blank line must separate subject and
/// body; every non-blank body line must be a hyphen bullet; at most
/// <see cref="CommitRules.MaxBullets"/> bullets; each bullet at most
/// <see cref="CommitRules.MaxBulletWords"/> words. Returns the bullet texts (the
/// content after <c>"- "</c>) so the concept check can scan them.
/// </summary>
internal static class BodyRules
{
    public static IReadOnlyList<string> Check(IReadOnlyList<string> bodyLines, List<Violation> violations)
    {
        // No body beyond the subject: nothing to check.
        if (bodyLines.All(line => line.Trim().Length == 0))
            return [];

        if (bodyLines.Count > 0 && bodyLines[0].Trim().Length != 0)
        {
            violations.Add(new Violation("a blank line must separate the subject from the body"));
        }

        var bullets = new List<string>();
        foreach (var line in bodyLines)
        {
            if (line.Trim().Length == 0)
                continue;

            if (!line.StartsWith(CommitRules.BulletPrefix, StringComparison.Ordinal))
            {
                violations.Add(new Violation(
                    $"body line must be a hyphen bullet ('- …'), not prose: \"{line}\""));
                continue;
            }

            var text = line[CommitRules.BulletPrefix.Length..];
            bullets.Add(text);
            CheckBulletWords(text, violations);
        }

        if (bullets.Count > CommitRules.MaxBullets)
        {
            violations.Add(new Violation(
                $"body must have at most {CommitRules.MaxBullets} bullets (found {bullets.Count})"));
        }

        return bullets;
    }

    private static void CheckBulletWords(string text, List<Violation> violations)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > CommitRules.MaxBulletWords)
        {
            violations.Add(new Violation(
                $"bullet must have at most {CommitRules.MaxBulletWords} words (found {words.Length}): \"{text}\""));
        }
    }
}
