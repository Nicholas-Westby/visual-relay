using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// The authoritative verify worktree (stages 9/10) is a detached-HEAD checkout
/// plus an overlay of uncommitted-not-ignored files. That OMITS everything git
/// ignores (node_modules, .env, .venv, dist), so the project's test command can
/// no longer resolve dependencies or read config — every test errors on import
/// and verify can never pass. The fix SYMLINKS each top-level git-ignored entry
/// from the source repo into the worktree so the runtime content is present,
/// while cleanup must remove ONLY the links (never the real targets).
///
/// These tests drive the otherwise-private CreateVerifyWorktreeAsync /
/// CleanupVerifyWorktreeAsync through the minimal internal test seam added to
/// <see cref="RelayDriver"/> (CreateVerifyWorktreeForTestAsync /
/// CleanupVerifyWorktreeForTestAsync), using the real GitInvoker against a
/// temp `git init` repo (same pattern as NonoRollbackSkipDirs / WorktreeResetter).
/// </summary>
public sealed class VerifyWorktreeIgnoredOverlayTests
{
    private static RelayDriver NewDriver() =>
        new(RelayDriverDependencies.ForTests(
            new ScriptedSubagentRunner(),
            new ScriptedTestRunner(),
            new InMemoryRelayEventSink()));

    /// <summary>Initialise a git repo with a committed tracked file.</summary>
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
    // 1. Ignored dir + ignored file are symlinked; tracked file present.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_SymlinksTopLevelIgnoredDirAndFile_ResolvingToSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-overlay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "node_modules/\n.env\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked-content");
            // Ignored dir with a dependency file inside.
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "dep"));
            await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "dep", "index.js"), "module.exports = 42;");
            // Ignored file (config).
            await File.WriteAllTextAsync(Path.Combine(root, ".env"), "SECRET=abc");
            CommitAll(root, "seed"); // commits .gitignore + tracked.txt only (rest ignored)

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-overlay", "run-overlay", CancellationToken.None);

            // Tracked file present in the checkout.
            Assert.True(File.Exists(Path.Combine(worktree, "tracked.txt")));

            // node_modules and .env exist in the worktree as SYMLINKS.
            var nmLink = Path.Combine(worktree, "node_modules");
            var envLink = Path.Combine(worktree, ".env");
            Assert.True(Directory.Exists(nmLink), "node_modules should be present in worktree");
            Assert.True(File.Exists(envLink), ".env should be present in worktree");
            Assert.True(new DirectoryInfo(nmLink).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "node_modules should be a symlink (reparse point), not a real copy");
            Assert.True(new FileInfo(envLink).Attributes.HasFlag(FileAttributes.ReparsePoint),
                ".env should be a symlink (reparse point), not a real copy");

            // The dependency is READABLE through the worktree path (resolves to source).
            var depThroughWorktree = await File.ReadAllTextAsync(
                Path.Combine(worktree, "node_modules", "dep", "index.js"));
            Assert.Equal("module.exports = 42;", depThroughWorktree);
            Assert.Equal("SECRET=abc", await File.ReadAllTextAsync(envLink));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. SAFETY INVARIANT — cleanup removes the LINKS, never the TARGETS.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupVerifyWorktree_RemovesLinksOnly_SourceTargetsSurviveWithContents()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "node_modules/\n.env\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "dep"));
            var depFile = Path.Combine(root, "node_modules", "dep", "index.js");
            await File.WriteAllTextAsync(depFile, "REAL-DEP");
            var envFile = Path.Combine(root, ".env");
            await File.WriteAllTextAsync(envFile, "REAL-ENV");
            CommitAll(root, "seed");

            var worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-safety", "run-safety", CancellationToken.None);
            // Sanity: the links were created.
            Assert.True(Directory.Exists(Path.Combine(worktree, "node_modules")));

            await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);

            // The worktree (and its links) are gone.
            Assert.False(Directory.Exists(worktree), "worktree directory should be removed");

            // CRITICAL: the SOURCE targets STILL EXIST with their original contents —
            // cleanup must remove the symlinks, NOT follow them and delete the real nodes.
            Assert.True(File.Exists(depFile), "source node_modules/dep/index.js must survive cleanup");
            Assert.True(File.Exists(envFile), "source .env must survive cleanup");
            Assert.Equal("REAL-DEP", await File.ReadAllTextAsync(depFile));
            Assert.Equal("REAL-ENV", await File.ReadAllTextAsync(envFile));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. VR/VCS-internal ignored dirs are NOT symlinked.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_DoesNotSymlinkVrInternalDirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-internal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            // .relay is git-ignored but VR-internal — the worktree manages its own.
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".relay/\nnode_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            Directory.CreateDirectory(Path.Combine(root, ".relay"));
            await File.WriteAllTextAsync(Path.Combine(root, ".relay", "state.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "x.js"), "x");
            CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-internal", "run-internal", CancellationToken.None);

            // node_modules IS symlinked (runtime content)...
            Assert.True(Directory.Exists(Path.Combine(worktree, "node_modules")),
                "node_modules should be symlinked");
            // ...but .relay is NOT — it is excluded as a VR-internal dir.
            Assert.False(Directory.Exists(Path.Combine(worktree, ".relay")),
                ".relay must NOT be symlinked into the verify worktree");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 4. A dir that is BOTH tracked and ignored is NOT top-level symlinked.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_PartiallyTrackedDir_NotSymlinked_AndDoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            // data/ has a tracked file (data/keep.txt) AND an ignored subdir (data/cache/).
            // git ls-files --ignored --directory reports the nested "data/cache/", not "data/",
            // so the top-level filter must skip it (no top-level "data" symlink).
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "data/cache/\n");
            Directory.CreateDirectory(Path.Combine(root, "data", "cache"));
            await File.WriteAllTextAsync(Path.Combine(root, "data", "keep.txt"), "keep");
            await File.WriteAllTextAsync(Path.Combine(root, "data", "cache", "blob.bin"), "cached");
            CommitAll(root, "seed"); // commits data/keep.txt + .gitignore

            var ex = await Record.ExceptionAsync(async () =>
                worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-partial", "run-partial", CancellationToken.None));
            Assert.Null(ex);
            Assert.NotNull(worktree);

            // The tracked file is checked out.
            Assert.True(File.Exists(Path.Combine(worktree!, "data", "keep.txt")));
            // data/ exists (from the checkout) but must NOT be a symlink — it is partially
            // checked out, not overlaid as a whole.
            var dataDir = new DirectoryInfo(Path.Combine(worktree!, "data"));
            Assert.True(dataDir.Exists);
            Assert.False(dataDir.Attributes.HasFlag(FileAttributes.ReparsePoint),
                "partially-tracked data/ must NOT be replaced by a top-level symlink");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
