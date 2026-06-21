namespace VisualRelay.Core.CommitLint;

/// <summary>
/// Structural checks over the subject line: a valid <c>type(scope)!: …</c>
/// prefix, length, no trailing period, and lowercase after the prefix.
/// </summary>
internal static class SubjectRules
{
    public static void Check(string subject, List<Violation> violations)
    {
        if (!CommitRules.SubjectPrefix.IsMatch(subject))
        {
            // Either no prefix at all, an unknown/uppercase type, or a bad
            // scope charset. Disambiguate so the message is actionable.
            violations.Add(new Violation(DescribePrefixFailure(subject)));
        }
        else
        {
            CheckLowercaseAfterPrefix(subject, violations);
        }

        if (subject.Length > CommitRules.MaxSubjectChars)
        {
            violations.Add(new Violation(
                $"subject exceeds {CommitRules.MaxSubjectChars} characters ({subject.Length})"));
        }

        if (subject.EndsWith('.'))
        {
            violations.Add(new Violation("subject must not end with a period"));
        }
    }

    private static void CheckLowercaseAfterPrefix(string subject, List<Violation> violations)
    {
        var match = CommitRules.FirstCharAfterPrefix.Match(subject);
        if (match is { Success: true, Groups.Count: > 2 })
        {
            var firstChar = match.Groups[2].Value;
            if (firstChar.Length == 1 && char.IsUpper(firstChar[0]))
            {
                violations.Add(new Violation(
                    "subject description must start with a lowercase letter after the type prefix"));
            }
        }
    }

    private static string DescribePrefixFailure(string subject)
    {
        var colon = subject.IndexOf(':');
        if (colon > 0)
        {
            var head = subject[..colon];
            var paren = head.IndexOf('(');
            var type = paren >= 0 ? head[..paren] : head.TrimEnd('!');
            if (!CommitRules.Types.Contains(type))
            {
                return $"subject type must be one of: {string.Join(", ", CommitRules.Types)} (got '{type}')";
            }

            // The type is known, so the prefix shape (scope charset / spacing /
            // empty description) is what failed.
            return "subject scope must match [a-z0-9._-]+ and be followed by ': ' and a description";
        }

        return $"subject must start with a Conventional-Commit prefix, e.g. 'feat: …' "
            + $"(types: {string.Join(", ", CommitRules.Types)})";
    }
}
