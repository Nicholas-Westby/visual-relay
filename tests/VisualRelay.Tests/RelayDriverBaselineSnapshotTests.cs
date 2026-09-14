using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The baseline verify asks whether a red verify's failures were there before the task. It
/// answered by stashing the checkout and running the suite in it, so for as long as the suite
/// ran (five to eleven minutes on max-sixty/worktrunk) the checkout held none of the task's work,
/// a process that died meanwhile left the work in the stash, and the base ran at a different
/// path, with a different overlay, from the verify snapshot it was compared with. The base now
/// runs in its own snapshot of the run's base commit, as the guard probe's does.
/// </summary>
public sealed class RelayDriverBaselineSnapshotTests
{
    [Fact]
    public async Task BaselineVerify_RunsTheBaseCommitInASnapshot_AndLeavesTheCheckoutAlone()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", [], baselineVerify: true, enableFixVerify: false);
        repo.WriteTask("base-in-snapshot", "# Base in a snapshot\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");
        var tests = new TreeRecordingTestRunner(repo.Root,
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed OldTest"),   // stage 10 verify, in its snapshot
            new TestRunResult(1, "Failed OldTest"),   // stage 10 retry
            new TestRunResult(1, "Failed OldTest"));  // the baseline
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new PrematureImplementationRunner(), tests, new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "base-in-snapshot");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Equal(4, tests.Calls.Count);
        var baseline = tests.Calls[3];
        Assert.NotEqual(repo.Root, baseline.RootPath);
        Assert.Equal("old\n", baseline.StatusInRoot);
        Assert.Equal("new\n", baseline.StatusInCheckout);
        Assert.DoesNotContain("relay-redgate", (await sim.Git(repo.Root, "stash", "list")).Output);
    }

    [Fact]
    public async Task BaselineVerify_OnAFailureTheBaseDoesNotHave_StillFlagsItAsNew()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", [], baselineVerify: true, enableFixVerify: false);
        repo.WriteTask("new-in-snapshot", "# New failure\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");
        var tests = new TreeRecordingTestRunner(repo.Root,
            new TestRunResult(1, "red"),                              // stage 5 author gate
            new TestRunResult(1, "Failed OldTest\nFailed NewTest"),   // stage 10 verify
            new TestRunResult(1, "Failed OldTest\nFailed NewTest"),   // stage 10 retry
            new TestRunResult(1, "Failed OldTest"));                  // the baseline
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new PrematureImplementationRunner(), tests, new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "new-in-snapshot");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Equal("new test failures: NewTest", outcome.Reason);
        Assert.NotEqual(repo.Root, tests.Calls[^1].RootPath);
    }

    /// <summary>
    /// The base is the commit the run started from. An agent that commits its own work moves HEAD,
    /// and a baseline taken there would hold the change it is meant to be compared against.
    /// </summary>
    [Fact]
    public async Task BaselineVerify_AfterTheAgentCommittedItsWork_RunsTheRunsBaseNotHead()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", [], baselineVerify: true, enableFixVerify: false);
        repo.WriteTask("self-commit", "# Self commit\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, ".gitignore", ".relay/\n");
        sim.Seed(repo.Root, "src/status.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");
        var tests = new TreeRecordingTestRunner(repo.Root,
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed OldTest"),   // stage 10 verify, in its snapshot
            new TestRunResult(1, "Failed OldTest"),   // stage 10 retry
            new TestRunResult(1, "Failed OldTest"));  // the baseline
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new SelfCommittingRunner(sim, repo.Root), tests, new InMemoryRelayEventSink(), sim),
            new RelayDriverOptions(CreateGitCommit: true));

        await driver.RunTaskAsync(repo.Root, "self-commit");

        Assert.Equal("old\n", tests.Calls[3].StatusInRoot);
    }

    /// <summary>The premature-implementation happy path, committing its implementation itself at stage 6.</summary>
    private sealed class SelfCommittingRunner(VisualRelay.GitSim.GitSim sim, string root) : ISubagentRunner
    {
        private readonly PrematureImplementationRunner _inner = new();

        public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            var result = await _inner.RunAsync(invocation, cancellationToken);
            if (invocation.Stage.Number == 6)
            {
                sim.Seed(root, "src/status.cs", "new\n");
                sim.Commit(root, "feat: implement status");
            }
            return result;
        }
    }

    /// <summary>Scripted results, recording where each run happened and what the tree held there and in the checkout.</summary>
    private sealed class TreeRecordingTestRunner(string checkoutRoot, params TestRunResult[] results) : ITestRunner
    {
        private readonly ScriptedTestRunner _inner = new(results);

        public List<(string RootPath, string? StatusInRoot, string? StatusInCheckout)> Calls { get; } = [];

        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
        {
            Calls.Add((rootPath, Status(rootPath), Status(checkoutRoot)));
            return _inner.RunAsync(rootPath, command, cancellationToken);
        }

        private static string? Status(string root)
        {
            var path = Path.Combine(root, "src", "status.cs");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
