namespace VisualRelay.Core.CommitLint;

/// <summary>
/// Validates a commit message against the Conventional-Commit ruleset ported
/// from ai-sorcery's <c>check-commit-message.ts</c> and reconciled with this
/// repo's fixed type set. Pure and IO-free: all inputs that require git/file IO
/// (changed-file basenames, the disallowed-substring list, the rule tier) are
/// supplied via <see cref="CommitLintContext"/>.
/// </summary>
public static class CommitMessageValidator
{
    /// <summary>
    /// Returns every violation in <paramref name="message"/> under the tier and
    /// inputs of <paramref name="context"/>. An empty list means the message
    /// passes. The message is preprocessed (comments, trailers, trailing blanks
    /// stripped) before any rule runs, so trailers are exempt.
    /// </summary>
    public static IReadOnlyList<Violation> Validate(string message, CommitLintContext context)
    {
        var violations = new List<Violation>();
        var (subject, bodyLines) = CommitMessagePreprocessor.Preprocess(message);

        CheckEmDash(subject, bodyLines, violations);
        SubjectRules.Check(subject, violations);
        var bullets = BodyRules.Check(bodyLines, violations);

        if (context.Tier == RuleTier.Human)
        {
            ConceptCheck.Check(subject, bullets, context, violations);
            CheckDisallowed(message, context, violations);
        }

        return violations;
    }

    private static void CheckEmDash(
        string subject, IReadOnlyList<string> bodyLines, List<Violation> violations)
    {
        // Trailers are already stripped, so an em dash there is not counted —
        // faithful to the reference.
        if (subject.Contains(CommitRules.EmDash) || bodyLines.Any(l => l.Contains(CommitRules.EmDash)))
        {
            violations.Add(new Violation("message must not contain an em dash (—, U+2014)"));
        }
    }

    private static void CheckDisallowed(
        string message, CommitLintContext context, List<Violation> violations)
    {
        foreach (var needle in context.DisallowedSubstrings)
        {
            if (needle.Length == 0)
                continue;
            if (message.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new Violation($"message contains a disallowed substring: \"{needle}\""));
            }
        }
    }
}
