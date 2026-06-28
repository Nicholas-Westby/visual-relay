using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// The verify worktree (stages 9/10) is a detached-HEAD checkout overlaid with the
/// task's uncommitted changes. The add/modify overlay must mirror the agent's working
/// tree REGARDLESS of whether a change is staged: a code review found it enumerated
/// modified files with <c>git diff --name-only</c> (NO <c>HEAD</c>), which sees only
/// UNSTAGED changes — so a STAGED edit silently kept HEAD's content, and a STAGED
/// <c>git mv</c> (A new + D old) had its new path dropped entirely (neither the
/// unstaged diff nor <c>git ls-files --others</c> lists a staged-tracked path), making
/// the renamed file VANISH from the worktree. The fix enumerates against
/// <c>git diff HEAD</c> (working tree vs HEAD = staged + unstaged).
///
/// These tests drive the otherwise-private CreateVerifyWorktreeAsync through the
/// internal test seam using the real GitInvoker against a temp <c>git init</c> repo,
/// mirroring <see cref="VerifyWorktreeDeletionOverlayTests"/>.
/// </summary>
public sealed class VerifyWorktreeStagedOverlayTests
{
    private static RelayDriver NewDriver() =>
        new(RelayDriverDependencies.ForTests(
            new ScriptedSubagentRunner(),
            new ScriptedTestRunner(),
            new InMemoryRelayEventSink()));

    private static void InitRepo(string root)
    {
        TestGit.Run(root, "init", "-q");
        TestGit.Run(root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(root, "config", "user.name", "Visual Relay Tests");
    }

    private static void CommitAll(string root, string message)
    {
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-q", "-m", message);
    }

    // ───────────────────────────────────────────────────────────────────
    // 1. STAGED RENAME (`git mv`, then NOT committed). git reports this as a
    //    staged A new + D old; `git diff HEAD` lists the new path, the unstaged
    //    `git diff` lists NOTHING. The new path must be PRESENT (with content)
    //    and the old path ABSENT — the rename must not vanish or be half-applied.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_StagedRename_NewPathPresentWithContent_OldPathAbsent()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-staged-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "RENAME-CONTENT");
            CommitAll(root, "seed");

            // STAGED rename: git mv stages A new.txt + D old.txt and moves the file on disk.
            TestGit.Run(root, "mv", "old.txt", "new.txt");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-staged-rename", "run-staged-rename", CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(worktree, "new.txt")),
                "the staged-rename destination must be copied into the verify worktree (only git diff HEAD lists the staged add)");
            Assert.Equal("RENAME-CONTENT", await File.ReadAllTextAsync(Path.Combine(worktree, "new.txt")));
            Assert.False(File.Exists(Path.Combine(worktree, "old.txt")),
                "the staged-rename source must NOT be resurrected from HEAD in the verify worktree");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. STAGED MODIFICATION (edit then `git add`, NOT committed). The unstaged
    //    `git diff` shows nothing (working tree == index), so the overlay must
    //    key on `git diff HEAD` or it silently verifies HEAD's stale content.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_StagedModification_OverlayHasModifiedContentNotHead()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-staged-mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, "edited.txt"), "HEAD-CONTENT");
            CommitAll(root, "seed");

            // STAGED modification: edit then git add (staged, NOT committed).
            await File.WriteAllTextAsync(Path.Combine(root, "edited.txt"), "STAGED-CONTENT");
            TestGit.Run(root, "add", "edited.txt");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-staged-mod", "run-staged-mod", CancellationToken.None);

            Assert.Equal("STAGED-CONTENT", await File.ReadAllTextAsync(Path.Combine(worktree, "edited.txt")));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
