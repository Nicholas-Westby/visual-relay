namespace VisualRelay.Guards;

/// <summary>
/// Integrates <see cref="ShellScriptClassifier"/> and <see cref="ShellScriptLineCounter"/>
/// to find shell scripts whose logic-line count exceeds a limit.
/// </summary>
public static class ShellSizeGuard
{
    /// <summary>
    /// Describes a single violation.
    /// </summary>
    public sealed record Violation(string Path, int Count, int Limit);

    /// <summary>
    /// Returns an ordered list of violations for shell scripts whose logic-line count
    /// exceeds <paramref name="limit"/>. Non-shell files are silently ignored.
    /// Results are ordered by path (ordinal).
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(
        IReadOnlyList<(string Path, string[] Lines)> files,
        int limit)
    {
        var violations = new List<Violation>();

        foreach (var (path, lines) in files)
        {
            var firstLine = lines.Length > 0 ? lines[0] : null;
            if (!ShellScriptClassifier.IsShellScript(path, firstLine))
                continue;

            var count = ShellScriptLineCounter.CountLogicLines(lines);
            if (count > limit)
            {
                violations.Add(new Violation(path, count, limit));
            }
        }

        violations.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return violations;
    }
}
