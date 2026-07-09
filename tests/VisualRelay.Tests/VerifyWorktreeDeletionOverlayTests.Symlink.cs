using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed partial class VerifyWorktreeDeletionOverlayTests
{
    [Fact]
    public async Task CreateVerifyWorktree_DeletedDanglingSymlink_RemovedFromWorktree()
    {
        SlowIntegration.SkipIfNotOptedIn();
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-del-dangling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = new RelayDriver(RelayDriverDependencies.ForTests(
            new ScriptedSubagentRunner(), new ScriptedTestRunner(), new InMemoryRelayEventSink()));
        string? worktree = null;
        try
        {
            TestGit.Run(root, "init", "-q");
            TestGit.Run(root, "config", "user.email", "visual-relay@example.test");
            TestGit.Run(root, "config", "user.name", "Visual Relay Tests");
            // A tracked symlink whose target does NOT exist → dangling once checked out.
            File.CreateSymbolicLink(Path.Combine(root, "link"), "missing-target");
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "keep");
            TestGit.Run(root, "add", ".");
            TestGit.Run(root, "commit", "-q", "-m", "seed");

            // Staged removal of the (dangling) tracked symlink.
            TestGit.Run(root, "rm", "-q", "link");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-del-dangling", "run-del-dangling", CancellationToken.None);

            // A directory listing surfaces a dangling link (readdir), unlike File.Exists.
            var entries = Directory.EnumerateFileSystemEntries(worktree).Select(Path.GetFileName).ToList();
            Assert.DoesNotContain("link", entries);
            Assert.True(File.Exists(Path.Combine(worktree, "keep.txt")),
                "an unrelated tracked file must still be present in the verify worktree");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
