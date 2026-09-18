using VisualRelay.Cli;
using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Guard-as-test for <see cref="TestBudgetGuard"/>, which pins the parallel-mode
/// test watchdog default in <c>tools/VisualRelay.Cli/WatchdogTimeouts.cs</c> to 90s.
/// <para>
/// That number is a SPEED BUDGET, not a hang timeout, and it is the kind of constant
/// that drifts silently: raising it makes a slow suite look healthy and nothing else
/// complains. So it is pinned twice — here, and at commit time by
/// <c>.githooks/pre-commit</c>, which runs <c>./visual-relay guards test-budget</c>.
/// The inline-snippet tests below prove the matcher has teeth in both directions.
/// </para>
/// </summary>
public sealed class TestBudgetGuardTests
{
    private const string GuardedFile = "tools/VisualRelay.Cli/WatchdogTimeouts.cs";

    /// <summary>A <c>ForTest</c> body whose parallel-mode default is <paramref name="secs"/>.</summary>
    private static string ForTestSource(string secs) => $$"""
        namespace VisualRelay.Cli;
        public static class WatchdogTimeouts
        {
            public static TimeSpan ForTest(bool serial) =>
                Resolve(Environment.GetEnvironmentVariable("VISUAL_RELAY_TEST_TIMEOUT"), serial ? 1800 : {{secs}});
        }
        """;

    // ── Inline-snippet unit tests ──────────────────────────────────────────

    /// <summary>The budget itself, pinned as a constant so the guard and the docs cannot diverge.</summary>
    [Fact]
    public void ExpectedSeconds_Is90()
    {
        Assert.Equal(90, TestBudgetGuard.ExpectedSeconds);
        Assert.Equal(GuardedFile, TestBudgetGuard.GuardedPath);
    }

    /// <summary>Teeth: raising the budget — the failure mode this guard exists for.</summary>
    [Theory]
    [InlineData("120")]
    [InlineData("300")]
    [InlineData("91")]
    public void RaisedBudget_IsAViolation(string secs)
    {
        var violation = TestBudgetGuard.FindViolation(GuardedFile, ForTestSource(secs));

        Assert.NotNull(violation);
        Assert.Equal(secs, violation.Found);
        Assert.Equal(GuardedFile, violation.Path);
        Assert.Contains($"is {secs}s", violation.Reason, StringComparison.Ordinal);
    }

    /// <summary>Anything other than 90 is a violation, lowering it included.</summary>
    [Theory]
    [InlineData("60")]
    [InlineData("9")]
    [InlineData("900")]
    public void BudgetOtherThan90_IsAViolation(string secs)
    {
        Assert.NotNull(TestBudgetGuard.FindViolation(GuardedFile, ForTestSource(secs)));
    }

    /// <summary>Exactly 90 passes — including reformatted and reflowed spellings.</summary>
    [Theory]
    [InlineData("serial ? 1800 : 90")]
    [InlineData("serial?1800:90")]
    [InlineData("serial\n                ? 1800\n                : 90")]
    public void Budget90_IsClean_WhateverTheFormatting(string expression)
    {
        var source = $"var t = Resolve(env, {expression});";

        Assert.Null(TestBudgetGuard.FindViolation(GuardedFile, source));
    }

    /// <summary>
    /// A 1800s serial default beside a 90s parallel default is clean: the guard reads
    /// only the else-branch, so the serial arm stays free to move.
    /// </summary>
    [Fact]
    public void SerialDefault_IsNotPinned()
    {
        Assert.Null(TestBudgetGuard.FindViolation(GuardedFile, ForTestSource("90")));
        Assert.Null(TestBudgetGuard.FindViolation(GuardedFile,
            "Resolve(env, serial ? 2400 : 90)"));
    }

    /// <summary>
    /// Refactoring the expression away is a violation too: the pin cannot be retired
    /// by deleting what it reads. Whoever moves the literal moves the guard with it.
    /// </summary>
    [Fact]
    public void MissingExpression_IsAViolation()
    {
        var violation = TestBudgetGuard.FindViolation(GuardedFile,
            "public static TimeSpan ForTest(bool serial) => Resolve(env, DefaultSecs);");

        Assert.NotNull(violation);
        Assert.Equal(0, violation.Line);
        Assert.Contains("no `serial ?", violation.Reason, StringComparison.Ordinal);
    }

    /// <summary>The line number points at the literal, not at the top of the file.</summary>
    [Fact]
    public void Violation_ReportsTheLineOfTheLiteral()
    {
        var violation = TestBudgetGuard.FindViolation(GuardedFile, ForTestSource("120"));

        Assert.NotNull(violation);
        Assert.Equal(5, violation.Line);
    }

    /// <summary>
    /// The failure text has to do the teaching: name the file, call the number a speed
    /// budget rather than a hang timeout, and say that raising it is the wrong move.
    /// </summary>
    [Fact]
    public void FailureMessage_NamesTheFileAndRefusesTheEasyFix()
    {
        var violation = TestBudgetGuard.FindViolation(GuardedFile, ForTestSource("120"));
        var message = TestBudgetGuard.FailureMessage(violation!);

        Assert.Contains(GuardedFile, message, StringComparison.Ordinal);
        Assert.Contains("SPEED BUDGET", message, StringComparison.Ordinal);
        Assert.Contains("not a hang timeout", message, StringComparison.Ordinal);
        Assert.Contains("wrong direction", message, StringComparison.Ordinal);
        Assert.Contains("VISUAL_RELAY_TEST_TIMEOUT", message, StringComparison.Ordinal);
    }

    // ── Live-tree tests ────────────────────────────────────────────────────

    /// <summary>
    /// The enforcing gate: this checkout's watchdog default is 90s. The pre-commit
    /// hook runs the same check through <c>./visual-relay guards test-budget</c>.
    /// </summary>
    [Fact]
    public void LiveTree_CarriesThe90SecondBudget()
    {
        var path = TestBudgetGuard.GuardedFilePath(RepoSetup.Root);
        Assert.True(File.Exists(path), $"Missing: {path}");

        var violation = TestBudgetGuard.FindViolation(
            TestBudgetGuard.GuardedPath, File.ReadAllText(path));

        Assert.True(violation is null,
            violation is null ? "" : TestBudgetGuard.FailureMessage(violation));
    }

    /// <summary>
    /// The behavioural half: with no per-run override, <c>./visual-relay test</c>
    /// really does get 90s. Skipped when the override is set, because honouring it is
    /// the documented escape hatch — and reading the env var beats mutating it, which
    /// this suite bans.
    /// </summary>
    [Fact]
    public void ForTest_ResolvesToTheBudget_WhenNoEnvOverride()
    {
        Assert.SkipWhen(
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VISUAL_RELAY_TEST_TIMEOUT")),
            "VISUAL_RELAY_TEST_TIMEOUT is set for this run — the per-run override wins by design");

        Assert.Equal(TimeSpan.FromSeconds(TestBudgetGuard.ExpectedSeconds),
            WatchdogTimeouts.ForTest(serial: false));
        Assert.Equal(TimeSpan.FromSeconds(1800), WatchdogTimeouts.ForTest(serial: true));
    }

    /// <summary>
    /// The commit-time half is actually wired: the pre-commit hook invokes the
    /// <c>test-budget</c> guard, and guards it with an existence test so target repos
    /// and the hook's own fixtures still commit.
    /// </summary>
    [Fact]
    public void PreCommitHook_RunsTheTestBudgetGuard()
    {
        var path = Path.Combine(RepoSetup.Root, ".githooks", "pre-commit");
        Assert.True(File.Exists(path), $"Missing: {path}");
        var content = File.ReadAllText(path);

        Assert.Contains("guards test-budget", content, StringComparison.Ordinal);
        Assert.Contains(GuardedFile, content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both documented statements of the budget agree with the constant, so a raise
    /// cannot leave the prose saying something else.
    /// </summary>
    [Theory]
    [InlineData("TROUBLESHOOTING.md", "The 90s default is a deliberate speed budget")]
    [InlineData(GuardedFile, "The 90s is a deliberate SPEED BUDGET")]
    public void DocsStateTheSameBudget(string relativePath, string expected)
    {
        var path = Path.Combine(RepoSetup.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing: {path}");

        Assert.Contains(expected, File.ReadAllText(path), StringComparison.Ordinal);
    }
}
