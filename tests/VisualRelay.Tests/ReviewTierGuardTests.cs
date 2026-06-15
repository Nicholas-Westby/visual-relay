using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class ReviewTierGuardTests
{
    // The Review stage (stage 7) is a quality gate. It must ALWAYS run on the
    // frontier tier — relying on a cheaper/inferior model there lets a weaker
    // reviewer rubber-stamp a flawed or silently de-scoped diff, degrading the
    // deliverable (this is why the review-tier-escalation experiment was reverted).
    // This guard fails the build if any future change lowers the Review tier.
    [Fact]
    public void ReviewStage_AlwaysRunsOnFrontierTier()
    {
        var review = RelayStages.All.Single(s => s.Name == "Review");
        Assert.Equal("frontier", review.Tier);
    }

    // The down-shift feature only ever LOWERS the Implement tier at runtime via
    // `stage with { Tier = "cheap" }` — it NEVER changes the canonical table.
    // This guard fails the build if a future change lowers the table-level tier,
    // because a table change could cascade to non-down-shifted runs.
    [Fact]
    public void DefaultImplementTierIsBalanced()
    {
        var implement = RelayStages.All.Single(s => s.Name == "Implement");
        Assert.Equal("balanced", implement.Tier);
    }
}
