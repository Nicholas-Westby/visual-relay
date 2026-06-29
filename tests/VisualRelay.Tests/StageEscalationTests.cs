using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The general-purpose escalation ladder shared by both escalation consumers
/// (the in-process <c>SwivalSubagentRunner.RunAsync</c> retry loop and the
/// driver's fix-verify loop): a 1-based run index maps to a model tier (stepped
/// up cheap→balanced→frontier, capped) and a turn/ceiling multiplier (doubled per
/// run, or held flat under the 10× boost). Pure — no VR/test-framework specifics.
/// </summary>
public sealed class StageEscalationTests
{
    [Theory]
    [InlineData("cheap", 1, "cheap")]
    [InlineData("cheap", 2, "balanced")]
    [InlineData("cheap", 3, "frontier")]
    [InlineData("balanced", 1, "balanced")]
    [InlineData("balanced", 2, "frontier")]
    [InlineData("balanced", 3, "frontier")]
    [InlineData("frontier", 1, "frontier")]
    [InlineData("frontier", 2, "frontier")]
    [InlineData("frontier", 3, "frontier")]
    public void TierForRun_StepsUpFromDefaultTier_CappedAtFrontier(string defaultTier, int run, string expected)
    {
        Assert.Equal(expected, StageEscalation.TierForRun(defaultTier, run));
    }

    [Fact]
    public void NextTier_Frontier_StaysFrontier()
    {
        Assert.Equal("frontier", StageEscalation.NextTier("frontier"));
    }

    [Theory]
    [InlineData(1, 200)]
    [InlineData(2, 400)]
    [InlineData(3, 800)]
    public void TurnsForRun_NonBoost_DoublesPerRun(int run, int expected)
    {
        Assert.Equal(expected, StageEscalation.TurnsForRun(200, run, flatBoost: false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TurnsForRun_FlatBoost_StaysFlatAcrossRuns(int run)
    {
        // The 10× boost gives a flat effective base of 2000 (200 × 10); the
        // per-escalation doubling does NOT apply — turns stay flat across runs.
        Assert.Equal(2000, StageEscalation.TurnsForRun(2000, run, flatBoost: true));
    }

    [Fact]
    public void Scale_AtIntBoundary_SaturatesToMaxInsteadOfOverflowing()
    {
        Assert.Equal(int.MaxValue, StageEscalation.Scale(int.MaxValue, 4));
        Assert.Equal(int.MaxValue, StageEscalation.Scale(2_000_000_000, 2));
    }

    [Fact]
    public void DescribeTransition_MatchesTheRunLogSentenceShape()
    {
        Assert.Equal(
            "Stage 10 Fix-verify escalated (run 2/3): tier balanced→frontier, max-turns 200→400",
            StageEscalation.DescribeTransition(10, "Fix-verify", 2, 3, "balanced", "frontier", 200, 400));
    }
}
