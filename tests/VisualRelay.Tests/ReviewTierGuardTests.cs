using System.Linq;
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
}
