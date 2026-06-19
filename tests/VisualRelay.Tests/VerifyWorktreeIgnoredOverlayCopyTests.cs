using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Ignored-content overlay policy for the authoritative verify worktree: every
/// git-ignored runtime entry is COPIED into the worktree as a real, writable,
/// ISOLATED file/dir — NEVER symlinked.
///
/// Symlinking a git-ignored entry back to the source breaks any build/test that
/// WRITES to it: the link resolves OUT of the sandboxed worktree cwd, and nono
/// (--allow-cwd) makes everything outside the cwd READONLY, so the write fails
/// (EPERM / "readonly database") and the tool exits non-zero even when every test
/// passes. This bit BOTH small writable entries (TEST-TIMING.md, .test-tmp/) AND
/// large writable build caches (SwiftPM .build, Cargo target/, Gradle build/),
/// whose build databases are opened read-write. Copying keeps every write inside
/// the cwd and never mutates the source (the verify's "real repo is never polluted"
/// invariant). CopyDirectoryResilient is copy-on-write-backed (clonefile/reflink)
/// where the filesystem supports it, so even a large build dir clones cheaply.
///
/// Driven through the internal test seam (CreateVerifyWorktreeForTestAsync /
/// CleanupVerifyWorktreeForTestAsync) with the real GitInvoker against a temp
/// `git init` repo, mirroring <see cref="VerifyWorktreeIgnoredOverlayTests"/>.
/// </summary>
public sealed class VerifyWorktreeIgnoredOverlayCopyTests
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
    // 1. A git-ignored FILE is COPIED (not symlinked); writing the worktree
    //    copy does NOT change the source file — the write is isolated.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_IgnoredFile_IsCopied_WriteDoesNotTouchSource()
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
                root, "task-copyfile", "run-copyfile", CancellationToken.None);

            var worktreeTiming = Path.Combine(worktree, "TEST-TIMING.md");
            Assert.True(File.Exists(worktreeTiming), "ignored file should be present in worktree");
            // It is a REAL file, NOT a reparse point (symlink).
            Assert.False(new FileInfo(worktreeTiming).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "ignored file must be COPIED (a regular file), not symlinked");
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
    // 2. A git-ignored DIR is COPIED (regular dir, not a symlink); a file
    //    written inside the worktree copy does NOT appear in source.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_IgnoredDir_IsCopied_WriteDoesNotAppearInSource()
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
                root, "task-copydir", "run-copydir", CancellationToken.None);

            var worktreeTmp = Path.Combine(worktree, ".test-tmp");
            Assert.True(Directory.Exists(worktreeTmp), "ignored dir should be present in worktree");
            Assert.False(new DirectoryInfo(worktreeTmp).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "ignored dir must be COPIED (a regular dir), not symlinked");
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
    // 3. A LARGE git-ignored BUILD dir (SwiftPM .build, Cargo target, Gradle
    //    build) is COPIED — real, writable, ISOLATED — so a sandboxed verify
    //    can WRITE its build database inside the cwd. Regression: symlinking
    //    large ignored dirs pointed .build OUT to the source repo, which is
    //    READONLY under nono --allow-cwd, so `swift test` failed writing
    //    build.db (exit 1) even though every test passed — looping Fix-verify.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_LargeIgnoredBuildDir_IsCopiedWritableAndIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-builddir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".build/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // A build cache the build tool WRITES to (build.db). Sized well above the
            // old 64 MB-style "large dep" boundary it once would have been symlinked at.
            Directory.CreateDirectory(Path.Combine(root, ".build"));
            var sourceDb = Path.Combine(root, ".build", "build.db");
            await File.WriteAllBytesAsync(sourceDb, new byte[8 * 1024]);
            CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-builddir", "run-builddir", CancellationToken.None);

            var worktreeBuild = Path.Combine(worktree, ".build");
            Assert.True(Directory.Exists(worktreeBuild), "ignored build dir should be present in worktree");
            // It MUST be a REAL dir, NOT a symlink OUT to the (sandbox-readonly) source —
            // otherwise the build tool's writes (build.db) resolve outside the nono
            // --allow-cwd worktree and fail with a readonly error (exit 1) even though
            // every test passes, looping Fix-verify forever.
            Assert.False(new DirectoryInfo(worktreeBuild).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "a large git-ignored build dir must be COPIED (real, writable), not symlinked");
            // WRITE-ISOLATION: rewriting build.db and adding an artifact inside the
            // worktree must NOT touch the source repo.
            var worktreeDb = Path.Combine(worktreeBuild, "build.db");
            Assert.True(File.Exists(worktreeDb), "build dir contents must be copied across");
            await File.WriteAllTextAsync(worktreeDb, "REBUILT-IN-WORKTREE");
            await File.WriteAllTextAsync(Path.Combine(worktreeBuild, "new-artifact.o"), "obj");
            Assert.False(File.Exists(Path.Combine(root, ".build", "new-artifact.o")),
                "a new build artifact written in the worktree must not appear in the source");
            Assert.Equal(8 * 1024, new FileInfo(sourceDb).Length);
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 4. SAFETY INVARIANT — after create→cleanup, the source contents of an
    //    overlaid file AND an overlaid large dir survive intact (copies are
    //    worktree-local; the source is never written or deleted).
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupVerifyWorktree_AfterCopy_SourceContentsSurviveIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-copysafe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "TEST-TIMING.md\n.build/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // Ignored file → copied.
            var sourceTiming = Path.Combine(root, "TEST-TIMING.md");
            await File.WriteAllTextAsync(sourceTiming, "TIMING-DATA");
            // Large ignored build dir → copied.
            Directory.CreateDirectory(Path.Combine(root, ".build"));
            var depFile = Path.Combine(root, ".build", "build.db");
            await File.WriteAllBytesAsync(depFile, new byte[8 * 1024]);
            CommitAll(root, "seed");

            var worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-copysafe", "run-copysafe", CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(worktree, "TEST-TIMING.md")));
            Assert.True(Directory.Exists(Path.Combine(worktree, ".build")));

            await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            Assert.False(Directory.Exists(worktree), "worktree directory should be removed");

            // Both source entries survive cleanup untouched.
            Assert.True(File.Exists(sourceTiming), "source TEST-TIMING.md must survive cleanup");
            Assert.Equal("TIMING-DATA", await File.ReadAllTextAsync(sourceTiming));
            Assert.True(File.Exists(depFile), "source .build/build.db must survive cleanup");
            Assert.Equal(8 * 1024, new FileInfo(depFile).Length);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
