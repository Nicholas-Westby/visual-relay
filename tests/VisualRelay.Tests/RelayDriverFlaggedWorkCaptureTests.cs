using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A flag whose work could not be captured must say so where an operator looks, and
/// must tell the drain the tree holds the only copy. It used to do neither: in the
/// measured WSL run commit-tree refused, nothing was logged, and the next task ran
/// over the flagged task's verified work.
/// </summary>
public sealed class RelayDriverFlaggedWorkCaptureTests
{
    [Fact]
    public async Task AFlagWhoseCaptureIsRefused_WarnsWithTheStepAndWhatGitSaid_AndMarksTheOutcome()
    {
        using var repo = TestRepository.Create();
        var events = new InMemoryRelayEventSink();
        var driver = BuildFlaggingAtStage6(repo, events,
            git => new FailingGitStepInvoker(git, ["commit-tree"], 128, FlaggedWorkCaptureTests.NoIdentity));

        var outcome = await driver.RunTaskAsync(repo.Root, "uncaptured");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.True(outcome.WorkUncaptured);
        var warning = Assert.Single(events.Events, e => e.EventName == "flagged_work_capture_failed");
        Assert.Equal("warn", warning.Level);
        Assert.Equal(6, warning.StageNumber);
        Assert.Equal("commit-tree", warning.Data!["step"]);
        Assert.Contains("empty ident name", warning.Data["output"], StringComparison.Ordinal);
        // The flag itself is still recorded as before.
        Assert.Single(events.Events, e => e.EventName == "flagged");
    }

    [Fact]
    public async Task AFlagWhoseWorkIsCaptured_NeitherWarnsNorMarksTheOutcome()
    {
        using var repo = TestRepository.Create();
        var events = new InMemoryRelayEventSink();
        var driver = BuildFlaggingAtStage6(repo, events, git => git);

        var outcome = await driver.RunTaskAsync(repo.Root, "uncaptured");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "uncaptured", FlaggedWorkStore.BundleFileName)));
        Assert.False(outcome.WorkUncaptured);
        Assert.DoesNotContain(events.Events, e => e.EventName == "flagged_work_capture_failed");
    }

    /// <summary>
    /// A run that commits nothing records no run base, so there is nothing to capture;
    /// that is not a failure, and must not stop a drain.
    /// </summary>
    [Fact]
    public async Task AFlagWithNothingToCapture_NeitherWarnsNorMarksTheOutcome()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", [], enableFixVerify: false);
        repo.WriteTask("no-commit", "# No commit\n");
        var events = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new FlagAtStageSubagentRunner(flagAtStage: 6),
                new ScriptedTestRunner(new TestRunResult(1, "red")), events,
                RelayDriverTestHelpers.InitTestRepo(repo)),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-commit");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.False(outcome.WorkUncaptured);
        Assert.DoesNotContain(events.Events, e => e.EventName == "flagged_work_capture_failed");
    }

    /// <summary>A committing run that flags at Implement, over a git the caller may wrap.</summary>
    private static RelayDriver BuildFlaggingAtStage6(
        TestRepository repo, InMemoryRelayEventSink events, Func<IGitInvoker, IGitInvoker> wrapGit)
    {
        repo.WriteConfig("exit 0", [], enableFixVerify: false);
        repo.WriteTask("uncaptured", "# Uncaptured\n");
        var git = wrapGit(RelayDriverTestHelpers.InitTestRepo(repo));
        return new RelayDriver(
            RelayDriverDependencies.ForTests(new FlagAtStageSubagentRunner(flagAtStage: 6),
                new ScriptedTestRunner(new TestRunResult(1, "red")), events, git),
            new RelayDriverOptions(CreateGitCommit: true));
    }
}
