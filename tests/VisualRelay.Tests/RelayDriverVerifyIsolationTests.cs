using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Fidelity regression: asserts that the driver's happy-path tests exercise the
/// real isolated-verify machinery (verify worktree + dirty-set delta + cleanup)
/// rather than silently falling back to the in-place gate. If the sim is ever
/// swapped back to an unregistered <see cref="VisualRelay.GitSim.GitSim"/> this
/// test fails loudly instead of the suite quietly testing the wrong path.
/// </summary>
public sealed class RelayDriverVerifyIsolationTests
{
    [Fact]
    public async Task RunHappyPath_WithCommittedSim_ExercisesIsolatedVerifyWorktree()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("isolation-check", "# Isolation check\n");
        var sim = RelayDriverTestHelpers.InitTestRepo(repo);

        var recordingTestRunner = new RecordingTestRunner(
            new TestRunResult(1, "red"),    // stage 5 author gate — red (passes)
            new TestRunResult(0, "green")); // stage 10 verify gate — green

        var events = new InMemoryRelayEventSink();
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, recordingTestRunner,
                events, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "isolation-check");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Exactly 2 runner calls: stage 5 (author gate) + stage 10 (verify).
        Assert.Equal(2, recordingTestRunner.Calls.Count);

        // Call 1: stage-5 author gate runs in-place at repo.Root by design.
        Assert.Equal(repo.Root, recordingTestRunner.Calls[0].RootPath);

        // Call 2: stage-10 isolated verify worktree — path differs from repo.Root,
        // contains the visual-relay worktree temp segment, keeps the task's name out
        // of the path, and is named by the verify_snapshot_created event.
        var verifyRootPath = recordingTestRunner.Calls[1].RootPath;
        Assert.NotEqual(repo.Root, verifyRootPath);
        Assert.Contains("/visual-relay/wt/", verifyRootPath.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.DoesNotContain("isolation-check", verifyRootPath, StringComparison.Ordinal);
        var created = Assert.Single(events.Events, e => e.EventName == "verify_snapshot_created");
        Assert.Equal(verifyRootPath, created.Data!["worktree"]);
        Assert.Equal("isolation-check-verify-s10-a1", created.Data["snapshot"]);
        // Which arm made the snapshot's copies: the two lay a large ignored folder out to the
        // same rule, and only the event says which one ran.
        Assert.Equal("app", created.Data["overlay"]);
    }
}
