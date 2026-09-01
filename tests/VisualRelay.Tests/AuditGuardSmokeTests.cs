using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Smoke test that runs all four hermeticity-audit matchers over
/// <see cref="CachedSyntaxTreesFixture.AllTrees"/> and asserts each completes
/// without throwing. No count assertions — findings are informational and
/// must not turn the suite red.
/// </summary>
/// <param name="treesFixture">
/// The assembly-wide parsed-tree fixture, registered in
/// <c>TestModuleInitializer.cs</c>, that the four audit matchers below run over.
/// </param>
public sealed class AuditGuardSmokeTests(CachedSyntaxTreesFixture treesFixture)
{

    /// <summary>
    /// All four audit matchers run over the full tree and complete without
    /// throwing. Counts are not asserted — findings are diagnostic-only.
    /// </summary>
    [Fact]
    public void AllFourMatchers_OverAllTrees_CompleteWithoutThrowing()
    {
        var trees = treesFixture.AllTrees;

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
