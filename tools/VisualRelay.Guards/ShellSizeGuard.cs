namespace VisualRelay.Guards;

/// <summary>
/// Integrates <see cref="ShellScriptClassifier"/> and <see cref="ShellScriptLineCounter"/>
/// to find shell scripts whose logic-line count exceeds a limit.
/// </summary>
public static class ShellSizeGuard
{
    /// <summary>The default per-script logic-line limit (a ceiling, not a target).</summary>
    public const int DefaultLimit = 24;

    /// <summary>
    /// Fixed 100-line ceiling for the <c>visual-relay</c> bootstrap — the one
    /// structural carve-out. No env-var knob; all other scripts use the general limit.
    /// </summary>
    public const int BootstrapLimit = 100;

    /// <summary>
    /// The exact repo-relative path that qualifies for <see cref="BootstrapLimit"/>.
    /// Comparison is ordinal (<c>sub/visual-relay</c> does not match).
    /// </summary>
    public const string BootstrapPath = "visual-relay";

    /// <summary>The environment variable that overrides <see cref="DefaultLimit"/>.</summary>
    private const string LimitEnvVar = "VISUAL_RELAY_SHELL_LINE_LIMIT";

    /// <summary>
    /// Describes a single violation.
    /// </summary>
    public sealed record Violation(string Path, int Count, int Limit);

    /// <summary>
    /// Resolves the limit from <see cref="LimitEnvVar"/>, falling back to
    /// <see cref="DefaultLimit"/>. The enforcing test and the ad-hoc runner both
    /// call this so the gate and the report can never diverge.
    /// </summary>
    public static int ResolveLimit()
    {
        var env = Environment.GetEnvironmentVariable(LimitEnvVar);
        return int.TryParse(env, out var parsed) ? parsed : DefaultLimit;
    }

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

            var effectiveLimit = string.Equals(path, BootstrapPath, StringComparison.Ordinal)
                ? BootstrapLimit
                : limit;

            var count = ShellScriptLineCounter.CountLogicLines(lines);
            if (count > effectiveLimit)
            {
                violations.Add(new Violation(path, count, effectiveLimit));
            }
        }

        violations.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return violations;
    }
}
