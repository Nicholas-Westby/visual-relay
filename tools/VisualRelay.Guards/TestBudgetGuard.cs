using System.Text.RegularExpressions;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that pins the parallel-mode test watchdog default in
/// <c>tools/VisualRelay.Cli/WatchdogTimeouts.cs</c> to
/// <see cref="ExpectedSeconds"/>.
/// <para>
/// That number is a SPEED BUDGET, not a hang timeout: the suite is meant to finish
/// inside it, and a run that does not is meant to say so. It is the one constant in
/// this repo whose drift is silent — raising it makes a slow suite look healthy, and
/// nothing else fails — so it is pinned by the pre-commit hook (the
/// <c>test-budget</c> subcommand) and by <c>TestBudgetGuardTests</c> in the suite.
/// </para>
/// <para>
/// A file that no longer carries the expression is a violation too, so a refactor
/// cannot quietly retire the pin: whoever moves the literal updates this guard with it.
/// </para>
/// </summary>
public static class TestBudgetGuard
{
    /// <summary>The only value the parallel-mode default may hold.</summary>
    public const int ExpectedSeconds = 90;

    /// <summary>The repo-relative file that carries the budget.</summary>
    public const string GuardedPath = "tools/VisualRelay.Cli/WatchdogTimeouts.cs";

    /// <summary>
    /// The <c>ForTest</c> conditional: <c>serial ? 1800 : 90</c>. Group 1 is the
    /// parallel-mode default. Whitespace is free-form so reformatting does not
    /// read as a violation.
    /// </summary>
    private static readonly Regex ForTestDefault = new(
        @"serial\s*\?\s*\d+\s*:\s*(\d+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Describes the one thing this guard can find (1-based <paramref name="Line"/>,
    /// 0 when the expression is absent entirely).
    /// </summary>
    public sealed record Violation(string Path, int Line, string Found, string Reason);

    /// <summary>The absolute path of the guarded file inside <paramref name="repoRoot"/>.</summary>
    public static string GuardedFilePath(string repoRoot) =>
        Path.Combine(repoRoot, GuardedPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Returns the violation in <paramref name="source"/>, or <c>null</c> when the
    /// parallel-mode default is exactly <see cref="ExpectedSeconds"/>.
    /// </summary>
    public static Violation? FindViolation(string path, string source)
    {
        var match = ForTestDefault.Match(source);
        if (!match.Success)
        {
            return new Violation(path, 0, "(none)",
                $"the parallel-mode test watchdog default is gone: no `serial ? <serial> : {ExpectedSeconds}` "
                + "expression left to pin. Restore it, or move this guard to wherever the budget now lives.");
        }

        var found = match.Groups[1].Value;
        if (found == ExpectedSeconds.ToString())
            return null;

        return new Violation(path, LineOf(source, match.Groups[1].Index), found,
            $"the parallel-mode test watchdog default is {found}s, but the budget is {ExpectedSeconds}s.");
    }

    /// <summary>
    /// The operator-facing failure text: what is wrong, and why raising the number
    /// is the wrong move.
    /// </summary>
    public static string FailureMessage(Violation violation)
    {
        var where = violation.Line > 0 ? $"{violation.Path}:{violation.Line}" : violation.Path;
        return $"test-budget: {where} — {violation.Reason}\n"
            + $"  {ExpectedSeconds}s is a deliberate SPEED BUDGET, not a hang timeout: the suite is meant to\n"
            + "  finish inside it, and a run that does not is meant to say so. Raising it so a slow\n"
            + "  suite passes is the wrong direction — make the suite faster instead, or raise it for\n"
            + "  a single run with VISUAL_RELAY_TEST_TIMEOUT=<seconds> ./visual-relay test.";
    }

    private static int LineOf(string source, int index) =>
        source.AsSpan(0, index).Count('\n') + 1;
}
