using VisualRelay.Core.CommitLint;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// The sealed-commit candidate chain must preserve body bullets so a task's
/// required commit-message evidence (e.g. a measured test-time bullet) survives
/// from the Verify stage into the actual <c>git commit</c>.
/// </summary>
public sealed class RelayDriverCommitChainTests
{
    [Fact]
    public void BuildCommitChain_PreservesCandidateBodyBullets()
    {
        var (chain, _) = RelayDriver.BuildCommitChain(
            ["perf: speed up retry tests\n\n- test time dropped from 4s to 1.4s, saving 2.6s (file total)"],
            "my-task");

        Assert.Equal(2, chain.Count);
        Assert.Equal(
            "perf: speed up retry tests"
            + Environment.NewLine + Environment.NewLine
            + "- test time dropped from 4s to 1.4s, saving 2.6s (file total)",
            chain[0]);
        Assert.Equal("chore(relay): my-task", chain[1]);
    }

    [Fact]
    public void BuildCommitChain_DropsNonConventionalCandidates_KeepsFallback()
    {
        var (chain, _) = RelayDriver.BuildCommitChain(["not conventional\n\n- bullet"], "my-task");

        Assert.Equal(["chore(relay): my-task"], chain);
    }

    [Fact]
    public void BuildCommitChain_SubjectOnlyCandidates_UnchangedShape()
    {
        var (chain, _) = RelayDriver.BuildCommitChain(
            ["feat: add queue controls", "fix: Trailing period."], "my-task");

        Assert.Equal(
            ["feat: add queue controls", "fix: trailing period", "chore(relay): my-task"],
            chain);
    }

    /// <summary>
    /// Regression: the sanitizer used to silently word-chop overlong candidates,
    /// so the mangled first candidate always won and shadowed shorter intact
    /// candidates. Now an overlong candidate must be rejected so the chain falls
    /// through to the next fitting candidate.
    /// </summary>
    [Fact]
    public void BuildCommitChain_OverlongFirstFittingSecond_UsesSecondIntact()
    {
        // 79 chars after prefix — exceeds the 72-char limit.
        var overlong = "fix: this is a deliberately overlong commit subject that should exceed limit";
        Assert.True(overlong.Length > CommitRules.MaxSubjectChars, "test input must overflow");

        var fitting = "fix: short fitting subject";
        var (chain, advisories) = RelayDriver.BuildCommitChain([overlong, fitting], "test-task");

        // The second (fitting) candidate must be the first entry in the chain,
        // NOT a truncated version of the overlong one.
        Assert.Equal(fitting, chain[0]);
    }

    /// <summary>
    /// When every candidate overflows, the chain must still be non-empty:
    /// an ellipsis-truncated safety-net subject sits before the generic
    /// <c>chore(relay): test-task</c> fallback so the worst case is at least
    /// recognizable.
    /// </summary>
    [Fact]
    public void BuildCommitChain_AllOverlong_YieldsEllipsisSafetyNetBeforeFallback()
    {
        var overlong1 = "fix: first overlong subject that is definitely way too long for the limit";
        var overlong2 = "feat: second overlong subject also way too long to fit in seventy two chars";
        Assert.True(overlong1.Length > CommitRules.MaxSubjectChars);
        Assert.True(overlong2.Length > CommitRules.MaxSubjectChars);

        var (chain, advisories) = RelayDriver.BuildCommitChain([overlong1, overlong2], "test-task");

        // The chain must contain an ellipsis-truncated entry before the generic fallback.
        var ellipsisEntry = chain.FirstOrDefault(c => c.Contains('…'));
        Assert.NotNull(ellipsisEntry);
        Assert.True(ellipsisEntry!.Length <= CommitRules.MaxSubjectChars);

        // The generic chore(relay) fallback is the last entry.
        Assert.Equal("chore(relay): test-task", chain[^1]);

        // The ellipsis entry sits before the fallback (not last, which is generic).
        Assert.NotEqual(ellipsisEntry, chain[^1]);
    }
}
