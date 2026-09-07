using VisualRelay.Core.Execution;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

/// <summary>
/// Nothing under <c>.relay/</c> may reach a sealed commit. The two working-tree
/// staging calls carry a <c>:(exclude).relay</c> pathspec, but the squash's
/// content-preservation pass restores paths straight out of the rewound commits'
/// tree — so a bookkeeping file the agent self-committed would ride in past both.
/// </summary>
public sealed class GitCommitterRunBaseSquashRelayTests
{
    [Fact]
    public async Task CommitAsync_AgentSelfCommittedARelayArtifact_LeavesItOutOfTheSealedCommit()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "base");
        sim.Commit(repo.Root, "chore: seed");
        var runBase = sim.Head(repo.Root)!;

        // The agent self-commits its implementation AND Visual Relay's own
        // bookkeeping, which the run keeps writing under .relay/ as it goes.
        Write(repo, "src/app.cs", "implemented");
        Write(repo, ".relay/my-task/ledger.md", "stage 6 notes");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "wip");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            sim, CancellationToken.None, runBaseSha: runBase, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");

        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains("src/app.cs", committed);
        Assert.DoesNotContain(".relay/my-task/ledger.md", committed);
    }
}
