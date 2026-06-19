using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Copy-vs-symlink overlay policy for the authoritative verify worktree.
///
/// Symlinking EVERY git-ignored entry (the original fix) breaks any test that
/// WRITES to a git-ignored path: the symlink resolves OUT of the sandboxed
/// worktree cwd back into the source repo, and nono (--allow-cwd) correctly
/// refuses the cross-cwd write → EPERM, failing tests that would otherwise pass.
///
/// Fix: SMALL ignored entries (below a byte threshold) are COPIED into the
/// worktree — real, writable, isolated files/dirs whose writes stay inside the
/// cwd and never touch the source — while LARGE entries (e.g. node_modules) are
/// still SYMLINKED to avoid copying hundreds of MB per verify attempt. These
/// tests inject a LOW threshold so they never have to write 64 MB.
///
/// Driven through the internal test seam (CreateVerifyWorktreeForTestAsync /
/// CleanupVerifyWorktreeForTestAsync) with the real GitInvoker against a temp
/// `git init` repo, mirroring <see cref="VerifyWorktreeIgnoredOverlayTests"/>.
/// </summary>
public sealed class VerifyWorktreeIgnoredOverlayCopyTests
{
    /// <summary>A threshold low enough that small fixtures land below it but a
    /// padded "large" dir lands above it — so tests never write 64 MB.</summary>
    private const long LowThresholdBytes = 4 * 1024; // 4 KiB

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
    // 1. A small git-ignored FILE is COPIED (not symlinked); writing the
    //    worktree copy does NOT change the source file — the write is isolated.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_SmallIgnoredFile_IsCopied_WriteDoesNotTouchSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-copyfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            // TEST-TIMING.md is git-ignored AND written-to by tests.
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "TEST-TIMING.md\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            var sourceTiming = Path.Combine(root, "TEST-TIMING.md");
            await File.WriteAllTextAsync(sourceTiming, "ORIGINAL");
            CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-copyfile", "run-copyfile", CancellationToken.None, LowThresholdBytes);

            var worktreeTiming = Path.Combine(worktree, "TEST-TIMING.md");
            Assert.True(File.Exists(worktreeTiming), "small ignored file should be present in worktree");
            // It is a REAL file, NOT a reparse point (symlink).
            Assert.False(new FileInfo(worktreeTiming).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "small ignored file must be COPIED (a regular file), not symlinked");
            Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(worktreeTiming));

            // WRITE-ISOLATION: writing the worktree copy must NOT change the source.
            await File.WriteAllTextAsync(worktreeTiming, "MUTATED-IN-WORKTREE");
            Assert.Equal("MUTATED-IN-WORKTREE", await File.ReadAllTextAsync(worktreeTiming));
            Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(sourceTiming));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. A small git-ignored DIR is COPIED (regular dir, not a symlink);
    //    a file written inside the worktree copy does NOT appear in source.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_SmallIgnoredDir_IsCopied_WriteDoesNotAppearInSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-copydir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".test-tmp/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            Directory.CreateDirectory(Path.Combine(root, ".test-tmp", "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, ".test-tmp", "nested", "seed.txt"), "seed");
            CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-copydir", "run-copydir", CancellationToken.None, LowThresholdBytes);

            var worktreeTmp = Path.Combine(worktree, ".test-tmp");
            Assert.True(Directory.Exists(worktreeTmp), "small ignored dir should be present in worktree");
            Assert.False(new DirectoryInfo(worktreeTmp).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "small ignored dir must be COPIED (a regular dir), not symlinked");
            // The seeded content was copied across.
            Assert.True(File.Exists(Path.Combine(worktreeTmp, "nested", "seed.txt")));
            Assert.Equal("seed", await File.ReadAllTextAsync(Path.Combine(worktreeTmp, "nested", "seed.txt")));

            // WRITE-ISOLATION: a new file written in the worktree copy must NOT
            // appear in the source dir.
            await File.WriteAllTextAsync(Path.Combine(worktreeTmp, "output.txt"), "from-test");
            Assert.True(File.Exists(Path.Combine(worktreeTmp, "output.txt")));
            Assert.False(File.Exists(Path.Combine(root, ".test-tmp", "output.txt")),
                "a write inside the copied worktree dir must not appear in the source repo");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. A git-ignored dir ABOVE the (low) threshold is SYMLINKED — large
    //    deps (node_modules) stay cheaply shared, not copied.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_LargeIgnoredDir_IsSymlinked()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-largedir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "node_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "dep"));
            // Pad above the low threshold so it is treated as a large dep.
            await File.WriteAllBytesAsync(
                Path.Combine(root, "node_modules", "dep", "blob.bin"),
                new byte[LowThresholdBytes * 2]);
            CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-largedir", "run-largedir", CancellationToken.None, LowThresholdBytes);

            var nmLink = Path.Combine(worktree, "node_modules");
            Assert.True(Directory.Exists(nmLink), "large ignored dir should be present in worktree");
            Assert.True(new DirectoryInfo(nmLink).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "large ignored dir must remain a SYMLINK (reparse point), not be copied");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 4. SAFETY INVARIANT — after create→cleanup, BOTH the copied small file
    //    AND the large symlinked dir's source contents survive intact.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupVerifyWorktree_AfterCopyAndSymlink_SourceContentsSurviveIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-mixsafe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "TEST-TIMING.md\nnode_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // Small ignored file → copied.
            var sourceTiming = Path.Combine(root, "TEST-TIMING.md");
            await File.WriteAllTextAsync(sourceTiming, "TIMING-DATA");
            // Large ignored dir → symlinked.
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "dep"));
            var depFile = Path.Combine(root, "node_modules", "dep", "blob.bin");
            await File.WriteAllBytesAsync(depFile, new byte[LowThresholdBytes * 2]);
            CommitAll(root, "seed");

            var worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-mixsafe", "run-mixsafe", CancellationToken.None, LowThresholdBytes);
            Assert.True(File.Exists(Path.Combine(worktree, "TEST-TIMING.md")));
            Assert.True(Directory.Exists(Path.Combine(worktree, "node_modules")));

            await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            Assert.False(Directory.Exists(worktree), "worktree directory should be removed");

            // Both source entries survive: the copy's source untouched, the
            // symlink target NOT followed/deleted.
            Assert.True(File.Exists(sourceTiming), "source TEST-TIMING.md must survive cleanup");
            Assert.Equal("TIMING-DATA", await File.ReadAllTextAsync(sourceTiming));
            Assert.True(File.Exists(depFile), "source node_modules/dep/blob.bin must survive cleanup");
            Assert.Equal(LowThresholdBytes * 2, new FileInfo(depFile).Length);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
