using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Smoke test that runs all four hermeticity-audit matchers over
/// <see cref="CachedSyntaxTreesFixture.AllTrees"/> and asserts each completes
/// without throwing. No count assertions — findings are informational and
/// must not turn the suite red.
/// </summary>
public sealed class AuditGuardSmokeTests
{
    private readonly CachedSyntaxTreesFixture _trees;

    public AuditGuardSmokeTests(CachedSyntaxTreesFixture trees)
    {
        _trees = trees;
    }

    /// <summary>
    /// All four audit matchers run over the full tree and complete without
    /// throwing. Counts are not asserted — findings are diagnostic-only.
    /// </summary>
    [Fact]
    public void AllFourMatchers_OverAllTrees_CompleteWithoutThrowing()
    {
        var trees = _trees.AllTrees;

        var retryViolations = RetryDelayLoopsGuard.FindViolations(trees);
        Assert.NotNull(retryViolations);

        var diViolations = DiBypassGuard.FindViolations(trees);
        Assert.NotNull(diViolations);

        var waitViolations = RealWaitsGuard.FindViolations(trees);
        Assert.NotNull(waitViolations);

        var waitSuppressions = RealWaitsGuard.FindSuppressions(trees);
        Assert.NotNull(waitSuppressions);

        var sideEffectViolations = TestSideEffectsGuard.FindViolations(trees);
        Assert.NotNull(sideEffectViolations);
    }
}
