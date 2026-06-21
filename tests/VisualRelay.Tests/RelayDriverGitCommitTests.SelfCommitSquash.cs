using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverGitCommitTests
{
    /// <summary>
    /// End-to-end: when the agent SUCCESSFULLY self-commits mid-run (authorized
    /// case — no rejecting hook in the target repo), the driver's Commit stage
    /// must squash that bare commit (and any later working-tree edits) into a
    /// single sealed commit. The task must be exactly one sealed commit on top
    /// of the run-base, with no bare provenance-less commit left behind.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_WhenAgentSelfCommitsMidRun_SquashesIntoOneSealedCommit()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        RunGit(repo.Root, "init");
        RunGit(repo.Root, "config user.email visual-relay@example.test");
        RunGit(repo.Root, "config user.name \"Visual Relay Tests\"");
        RunGit(repo.Root, "add .");
        RunGit(repo.Root, "commit -m \"chore: seed repo\"");
        var runBase = RunGit(repo.Root, "rev-parse HEAD").Trim();

        var runner = new MidRunSelfCommittingRunner(repo.Root);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        // The agent's bare commit actually landed mid-run (the bug's precondition).
        Assert.True(runner.AgentCommitLanded, "test precondition: agent's mid-run commit must have landed");
        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);

        // Exactly ONE commit on top of the run-base — the bare commit is gone.
        Assert.Equal("1", RunGit(repo.Root, $"rev-list --count {runBase}..HEAD").Trim());

        // That single commit is the sealed one, parented on the run-base.
        Assert.Equal(runBase, RunGit(repo.Root, "rev-parse HEAD^").Trim());
        var message = RunGit(repo.Root, "log -1 --pretty=%B");
        Assert.Contains("Task: ship-status", message);
        Assert.Contains("Relay-Seal:", message);

        // Nothing lost: both the self-committed change and the later edit are present.
        var names = RunGit(repo.Root, "show --name-only --pretty=format: HEAD");
        Assert.Contains("src/status.cs", names);
        Assert.Contains("src/extra.cs", names);
        Assert.Equal("implemented", RunGit(repo.Root, "show HEAD:src/status.cs"));
        Assert.Equal("post-commit edit", RunGit(repo.Root, "show HEAD:src/extra.cs"));
    }
}
