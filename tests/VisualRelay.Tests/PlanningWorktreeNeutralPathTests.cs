using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// A throwaway worktree's path carries no words from the task. Measured with max-sixty/worktrunk on
/// the Mac: the verify snapshot lived under ".../20260914060808-keep-multi-line-branch-vars-intact-
/// when-wt-reads-them-with-git-config-get-regexp/keep-multi-line-...-verify-s10-a1", a test that
/// checks a generated shell script never contains "-wt" found it inside that path, and verify went
/// red on a change that was green in the checkout. The slug twice also made the path about 230
/// characters, past what a Unix socket under the worktree can use.
/// </summary>
public sealed class PlanningWorktreeNeutralPathTests
{
    [Fact]
    public async Task CreateAsync_KeepsTheTaskAndRunNamesOutOfTheWorktreePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-neutral-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? worktree = null;
        try
        {
            var sim = new GitSimEngine();
            sim.InitRepo(root);
            sim.Seed(root, "tracked.txt", "tracked");
            sim.Commit(root, "seed");

            worktree = await PlanningWorktree.CreateAsync(root, "keep-vars-when-wt-reads-them-verify-s10-a1",
                "20260914060808-keep-vars-when-wt-reads-them", new GitSimEngine(), CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(worktree, "tracked.txt")));
            Assert.DoesNotContain("wt-reads", worktree, StringComparison.Ordinal);
            Assert.DoesNotContain("20260914060808", worktree, StringComparison.Ordinal);
        }
        finally
        {
            if (worktree is not null)
                await PlanningWorktree.RemoveAsync(root, worktree, new GitSimEngine(), CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    [Fact]
    public async Task CreateAsync_GivesEachWorktreeOfARunItsOwnDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-neutral-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var created = new List<string>();
        try
        {
            var sim = new GitSimEngine();
            sim.InitRepo(root);
            sim.Seed(root, "tracked.txt", "tracked");
            sim.Commit(root, "seed");

            created.Add(await PlanningWorktree.CreateAsync(root, "task-a", "run-1", new GitSimEngine(), CancellationToken.None));
            created.Add(await PlanningWorktree.CreateAsync(root, "task-a-verify-s10-a1", "run-1", new GitSimEngine(), CancellationToken.None));

            Assert.NotEqual(created[0], created[1]);
            Assert.Equal(Path.GetDirectoryName(created[0]), Path.GetDirectoryName(created[1]));
        }
        finally
        {
            foreach (var worktree in created)
                await PlanningWorktree.RemoveAsync(root, worktree, new GitSimEngine(), CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
