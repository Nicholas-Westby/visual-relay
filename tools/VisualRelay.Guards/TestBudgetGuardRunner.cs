namespace VisualRelay.Guards;

/// <summary>
/// CLI runner for <see cref="TestBudgetGuard"/>: reads
/// <c>tools/VisualRelay.Cli/WatchdogTimeouts.cs</c> and exits 1 (printing the
/// failure text to stderr) unless the parallel-mode test watchdog default is
/// exactly <see cref="TestBudgetGuard.ExpectedSeconds"/>.
/// <para>
/// This is what <c>.githooks/pre-commit</c> runs, so it fails CLOSED: a missing or
/// unreadable guarded file is a violation, not a pass. The same check runs in the
/// suite as <c>TestBudgetGuardTests</c>.
/// </para>
/// </summary>
public static class TestBudgetGuardRunner
{
    public static int Run(string repoRoot)
    {
        var path = TestBudgetGuard.GuardedFilePath(repoRoot);

        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"test-budget: could not read {TestBudgetGuard.GuardedPath} ({ex.Message}). "
                + "The test-budget guard cannot vouch for the speed budget, so the commit is refused.");
            return 1;
        }

        var violation = TestBudgetGuard.FindViolation(TestBudgetGuard.GuardedPath, source);
        if (violation is null)
            return 0;

        Console.Error.WriteLine(TestBudgetGuard.FailureMessage(violation));
        return 1;
    }
}
