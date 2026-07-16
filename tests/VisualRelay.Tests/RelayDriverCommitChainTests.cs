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
        var chain = RelayDriver.BuildCommitChain(
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
        var chain = RelayDriver.BuildCommitChain(["not conventional\n\n- bullet"], "my-task");

        Assert.Equal(["chore(relay): my-task"], chain);
    }

    [Fact]
    public void BuildCommitChain_SubjectOnlyCandidates_UnchangedShape()
    {
        var chain = RelayDriver.BuildCommitChain(
            ["feat: add queue controls", "fix: Trailing period."], "my-task");

        Assert.Equal(
            ["feat: add queue controls", "fix: trailing period", "chore(relay): my-task"],
            chain);
    }
}
