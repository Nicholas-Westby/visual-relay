using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// The authoritative verify worktree (stages 9/10) is a detached-HEAD checkout
/// plus an overlay of the task's uncommitted changes. The overlay COPIES
/// modified + untracked files, but historically it did NOT apply DELETIONS — so a
/// tracked file the task removed was RESURRECTED from HEAD inside the worktree.
/// The agent's correct deletion was silently reverted, the suite ran against a
/// tree that still contained the deleted file, and a task that removed a file
/// whose symbols were also removed could NEVER pass verify (the file recompiled
/// against the missing symbol → build failure on every attempt).
///
/// These tests drive the otherwise-private CreateVerifyWorktreeAsync through the
/// internal test seam (CreateVerifyWorktreeForTestAsync) using the real
/// GitInvoker against a temp `git init` repo, mirroring
/// <see cref="VerifyWorktreeIgnoredOverlayTests"/>. They cover BOTH deletion
/// shapes git reports differently — a STAGED `git rm` (the observed live failure;
/// only `git diff HEAD` reveals it) and an UNSTAGED plain `rm` — plus directory
/// pruning and coexistence with the existing add/modify overlay.
/// </summary>
public sealed class VerifyWorktreeDeletionOverlayTests
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
    // 1. STAGED deletion (`git rm`) — the observed live failure. The file is
    //    removed from BOTH the index and the working tree, so neither
    //    `git ls-files --deleted` nor an unstaged `git diff` reveals it; only
    //    `git diff HEAD` does. The worktree must NOT resurrect it; an unrelated
    //    sibling tracked file must still be present.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_StagedGitRmDeletion_RemovesFileFromWorktree_KeepsSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-del-staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, "doomed.txt"), "delete me");
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "keep me");
            CommitAll(root, "seed");

            // STAGED deletion: removes the path from index AND working tree.
            TestGit.Run(root, "rm", "-q", "doomed.txt");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-del-staged", "run-del-staged", CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(worktree, "doomed.txt")),
                "a staged-deleted (git rm) tracked file must NOT be resurrected from HEAD in the verify worktree");
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

    // ───────────────────────────────────────────────────────────────────
    // 2. UNSTAGED deletion (plain `rm`, file gone from the working tree only).
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_UnstagedRmDeletion_RemovesFileFromWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-del-unstaged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, "doomed.txt"), "delete me");
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "keep me");
            CommitAll(root, "seed");

            // UNSTAGED deletion: removed from the working tree only (still in the index).
            File.Delete(Path.Combine(root, "doomed.txt"));

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-del-unstaged", "run-del-unstaged", CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(worktree, "doomed.txt")),
                "an unstaged-deleted (plain rm) tracked file must NOT be resurrected from HEAD in the verify worktree");
            Assert.True(File.Exists(Path.Combine(worktree, "keep.txt")));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. DELETED DIRECTORY — git tracks files, not dirs, so removing every file
    //    under a dir reports per-file deletions. The worktree must drop the files
    //    AND prune the now-empty parent dirs (git stores no empty dirs), so the
    //    snapshot matches the agent's working tree.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_WholeDirectoryDeletion_PrunesEmptyParentDirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-del-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            // deadpkg/ contains ONLY these (nested) files; removing them empties it entirely.
            Directory.CreateDirectory(Path.Combine(root, "deadpkg", "inner"));
            await File.WriteAllTextAsync(Path.Combine(root, "deadpkg", "inner", "a.txt"), "a");
            await File.WriteAllTextAsync(Path.Combine(root, "deadpkg", "inner", "b.txt"), "b");
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "keep");
            CommitAll(root, "seed");

            TestGit.Run(root, "rm", "-q", "-r", "deadpkg");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-del-dir", "run-del-dir", CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(worktree, "deadpkg", "inner", "a.txt")));
            Assert.False(File.Exists(Path.Combine(worktree, "deadpkg", "inner", "b.txt")));
            Assert.False(Directory.Exists(Path.Combine(worktree, "deadpkg", "inner")),
                "an emptied nested dir must be pruned");
            Assert.False(Directory.Exists(Path.Combine(worktree, "deadpkg")),
                "a fully-deleted directory must leave no empty husk in the verify worktree");
            Assert.True(File.Exists(Path.Combine(worktree, "keep.txt")));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 4. PARTIAL directory deletion — deleting one file in a dir must NOT prune
    //    the dir or its still-tracked siblings (no over-deletion).
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_PartialDirectoryDeletion_KeepsRemainingSiblingAndDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-del-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            Directory.CreateDirectory(Path.Combine(root, "pkg"));
            await File.WriteAllTextAsync(Path.Combine(root, "pkg", "gone.txt"), "gone");
            await File.WriteAllTextAsync(Path.Combine(root, "pkg", "stay.txt"), "stay");
            CommitAll(root, "seed");

            File.Delete(Path.Combine(root, "pkg", "gone.txt"));

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-del-partial", "run-del-partial", CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(worktree, "pkg", "gone.txt")),
                "the deleted file must be removed from the worktree");
            Assert.True(File.Exists(Path.Combine(worktree, "pkg", "stay.txt")),
                "a still-tracked sibling must survive");
            Assert.True(Directory.Exists(Path.Combine(worktree, "pkg")),
                "a partially-deleted dir (a sibling remains) must NOT be pruned");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 5. COEXISTENCE — a deletion applied alongside a MODIFICATION and an
    //    UNTRACKED addition: the deletion is dropped while the existing overlay
    //    behavior (agent's modified content + new untracked file) is preserved.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_DeletionWithModificationAndUntracked_AppliesAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-del-mix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, "doomed.txt"), "delete me");
            await File.WriteAllTextAsync(Path.Combine(root, "edited.txt"), "ORIGINAL");
            CommitAll(root, "seed");

            File.Delete(Path.Combine(root, "doomed.txt"));                                  // deletion
            await File.WriteAllTextAsync(Path.Combine(root, "edited.txt"), "AGENT-EDIT");    // modification
            await File.WriteAllTextAsync(Path.Combine(root, "added.txt"), "BRAND-NEW");      // untracked add

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-del-mix", "run-del-mix", CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(worktree, "doomed.txt")),
                "deletion must be applied");
            Assert.Equal("AGENT-EDIT", await File.ReadAllTextAsync(Path.Combine(worktree, "edited.txt")));
            Assert.Equal("BRAND-NEW", await File.ReadAllTextAsync(Path.Combine(worktree, "added.txt")));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 6. DANGLING tracked symlink deletion (cross-platform regression guard).
    //    The HEAD checkout resurrects a deleted symlink whose target is absent.
    //    On WINDOWS File.Exists AND Directory.Exists are BOTH false for such a
    //    broken link, so removal must key on the link's OWN attributes
    //    (ReparsePoint) or it survives; on macOS/Unix File.Exists already returns
    //    TRUE for a dangling link (verified empirically) so the File.Exists path
    //    removes it too. Either way the deleted link must end up ABSENT. NB: this
    //    therefore does NOT fail on macOS pre-fix — the broken-link removal is a
    //    Windows correctness fix — but it guards the behavior on both platforms.
    //    Presence is checked via a directory LISTING (readdir surfaces the link
    //    entry regardless of target), independent of the removal mechanism.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_DeletedDanglingSymlink_RemovedFromWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-del-dangling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            // A tracked symlink whose target does NOT exist → dangling once checked out.
            File.CreateSymbolicLink(Path.Combine(root, "link"), "missing-target");
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "keep");
            CommitAll(root, "seed");

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
