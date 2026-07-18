using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="RealWaitsGuard"/> — delegates to
/// <see cref="RealSleepGuard.FindViolations"/> across all three roots (src,
/// tests, tools), PLUS inventories every <c>// vr-allow-sleep: &lt;reason&gt;</c>
/// suppression with its reason so stale exemptions get re-reviewed.
/// </summary>
public sealed class RealWaitsGuardTests
{
    private readonly CachedSyntaxTreesFixture _trees;

    public RealWaitsGuardTests(CachedSyntaxTreesFixture trees)
    {
        _trees = trees;
    }

    /// <summary>
    /// A Thread.Sleep in a non-exempt file is found by the delegated RealSleepGuard
    /// and shows up as a real-waits violation.
    /// </summary>
    [Fact]
    public void RealSleep_IsReported_AsRealWait()
    {
        const string source = "class C { void M() => Thread.Sleep(500); }";

        var violations = RealWaitsGuard.FindViolations([("src/Fixtures/Sleeper.cs", source)]);

        Assert.NotEmpty(violations);
        Assert.Contains(violations, v => v.Reason.Contains("real-waits"));
    }

    /// <summary>
    /// A Task.Delay without TimeProvider in a non-exempt file is found.
    /// </summary>
    [Fact]
    public void TaskDelayWithoutTimeProvider_IsReported()
    {
        const string source = "class C { Task M() => Task.Delay(100); }";

        var violations = RealWaitsGuard.FindViolations([("src/Fixtures/Delayer.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// A sanctioned Task.Delay with TimeProvider is NOT reported.
    /// </summary>
    [Fact]
    public void TaskDelayWithTimeProvider_IsNotReported()
    {
        const string source =
            "class C { Task M(TimeProvider tp, CancellationToken ct) => Task.Delay(TimeSpan.FromMilliseconds(50), tp, ct); }";

        var violations = RealWaitsGuard.FindViolations([("src/Fixtures/Virtual.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A suppression marker <c>// vr-allow-sleep: integration test needs real settle</c>
    /// is inventoried with its reason. The sleep line itself is suppressed but the
    /// suppression entry appears in the output.
    /// </summary>
    [Fact]
    public void AllowSleepMarker_WithReason_IsInventoried()
    {
        const string source = """
            class C {
                void M() => Thread.Sleep(500); // vr-allow-sleep: integration test needs real settle
                void N() => Console.WriteLine("hello");
            }
            """;

        var suppressions = RealWaitsGuard.FindSuppressions([("src/Fixtures/Suppressed.cs", source)]);

        Assert.NotEmpty(suppressions);
        var s = Assert.Single(suppressions);
        Assert.Contains("integration test needs real settle", s.Reason);
    }

    /// <summary>
    /// A bare marker with no reason is still inventoried (not a valid suppression,
    /// but the audit shows it for completeness).
    /// </summary>
    [Fact]
    public void BareAllowSleepMarker_WithoutReason_IsStillInventoried()
    {
        const string source = """
            class C {
                void M() => Thread.Sleep(500); // vr-allow-sleep:
            }
            """;

        var suppressions = RealWaitsGuard.FindSuppressions([("src/Fixtures/Bare.cs", source)]);

        Assert.NotEmpty(suppressions);
    }

    /// <summary>
    /// Self-exempt files (RealSleepGuard's own fixtures) are excluded entirely.
    /// </summary>
    [Fact]
    public void RealSleepGuardSelfExempt_IsNotScanned()
    {
        const string source = "class C { void M() => Thread.Sleep(5000); }";

        var violations = RealWaitsGuard.FindViolations(
            [("tools/VisualRelay.Guards/RealSleepGuard.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Live-tree smoke: matcher completes without throwing over the full tree.
    /// </summary>
    [Fact]
    public void AllTrees_CompleteWithoutThrowing()
    {
        var violations = RealWaitsGuard.FindViolations(_trees.AllTrees);
        var suppressions = RealWaitsGuard.FindSuppressions(_trees.AllTrees);

        Assert.NotNull(violations);
        Assert.NotNull(suppressions);
    }
}
