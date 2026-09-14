using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Winding a cancelled run down has two jobs: RECORD what the run reached, and
/// RESTORE the tree it was editing. On a run-selected or a resume the driver's
/// restore is the only restore there is, so a failure in any recording step must
/// not be allowed to skip it. The one exception is a capture that could not save the
/// work: the tree then holds its only copy, and restoring would erase it.
/// </summary>
public sealed partial class RelayDriverCancelTests
{
    [Fact]
    public async Task RunTaskAsync_CancelledWithAFailingEventSink_StillRestoresTheTree()
    {
        using var repo = TestRepository.Create();
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, ".gitignore", ".relay/\n");
        sim.Seed(repo.Root, "src/app.cs", "committed\n");
        sim.Commit(repo.Root, "chore: seed repo");
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("restore-me", "# Restore me\n");
        using var cts = new CancellationTokenSource();
        var runner = new FileWritingSubagentRunner(
            new CancellingSubagentRunner(cts, atStage: 8), 6, "src/app.cs", "half-finished\n");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(
                new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new ThrowOnCancelledEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "restore-me", cts.Token);

        Assert.Equal("cancelled by operator", outcome.Reason);
        Assert.Equal("committed\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunTaskAsync_CancelledWhenTheCaptureIsRefused_WarnsMarksAndLeavesTheWorkInTheTree()
    {
        using var repo = TestRepository.Create();
        var events = new InMemoryRelayEventSink();

        var outcome = await CancelAtReviewAfterEditingAsync(repo, events,
            sim => new FailingGitStepInvoker(sim, ["commit-tree"], 128, FlaggedWorkCaptureTests.NoIdentity));

        Assert.Equal("cancelled by operator", outcome.Reason);
        Assert.True(outcome.WorkUncaptured);
        var warning = Assert.Single(events.Events, e => e.EventName == "flagged_work_capture_failed");
        Assert.Equal("commit-tree", warning.Data!["step"]);
        Assert.Equal("half-finished\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunTaskAsync_CancelledWithTheWorkCaptured_StillRestoresTheTree()
    {
        using var repo = TestRepository.Create();

        var outcome = await CancelAtReviewAfterEditingAsync(repo, new InMemoryRelayEventSink(), sim => sim);

        Assert.False(outcome.WorkUncaptured);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "keep-me", FlaggedWorkStore.BundleFileName)));
        Assert.Equal("committed\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), TestContext.Current.CancellationToken));
    }

    /// <summary>A committing run that edits <c>src/app.cs</c> at Implement and is cancelled at Visual-review.</summary>
    private static async Task<RelayTaskOutcome> CancelAtReviewAfterEditingAsync(
        TestRepository repo, IRelayEventSink events, Func<GitSim.GitSim, IGitInvoker> wrapGit)
    {
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, ".gitignore", ".relay/\n");
        sim.Seed(repo.Root, "src/app.cs", "committed\n");
        sim.Commit(repo.Root, "chore: seed repo");
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("keep-me", "# Keep me\n");
        using var cts = new CancellationTokenSource();
        var runner = new FileWritingSubagentRunner(
            new CancellingSubagentRunner(cts, atStage: 8), 6, "src/app.cs", "half-finished\n");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(
                new TestRunResult(1, "red"), new TestRunResult(0, "green")), events, wrapGit(sim)),
            new RelayDriverOptions(CreateGitCommit: true));
        return await driver.RunTaskAsync(repo.Root, "keep-me", cts.Token);
    }

    /// <summary>
    /// Fails exactly where a broken sink fails a real wind-down: on the
    /// <c>cancelled</c> event, the last recording step before the restore.
    /// </summary>
    private sealed class ThrowOnCancelledEventSink : IRelayEventSink
    {
        public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default) =>
            relayEvent.EventName == "cancelled"
                ? throw new IOException("event sink is broken")
                : Task.CompletedTask;
    }
}
