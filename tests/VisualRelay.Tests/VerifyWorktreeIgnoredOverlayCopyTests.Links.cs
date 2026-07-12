namespace VisualRelay.Tests;

public sealed partial class VerifyWorktreeIgnoredOverlayCopyTests
{
    // ───────────────────────────────────────────────────────────────────
    // 5. npm-shape regression (the express case): a relative file symlink
    //    inside a small ignored dir (e.g. node_modules/.bin/mocha ->
    //    ../mocha/bin/mocha) is PRESERVED — not dropped — so the test
    //    command can find its launchers in PATH.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_NpmShapeRelativeSymlink_IsPreservedAndResolves()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-npm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // Real executable inside a package dir.
            var pkgBin = Path.Combine(root, "deps", "pkg", "bin");
            Directory.CreateDirectory(pkgBin);
            await File.WriteAllTextAsync(Path.Combine(pkgBin, "tool"), "#!/bin/sh\necho ok\n");
            // npm-style relative symlink: deps/.bin/tool -> ../pkg/bin/tool
            var dotBin = Path.Combine(root, "deps", ".bin");
            Directory.CreateDirectory(dotBin);
            File.CreateSymbolicLink(Path.Combine(dotBin, "tool"), "../pkg/bin/tool");
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-npm", "run-npm", CancellationToken.None, LowThresholdBytes);

            var worktreeTool = Path.Combine(worktree, "deps", ".bin", "tool");
            Assert.True(File.Exists(worktreeTool),
                "relative symlink inside small copied dir must be present in worktree");
            var attrs = File.GetAttributes(worktreeTool);
            Assert.True(attrs.HasFlag(FileAttributes.ReparsePoint),
                ".bin/tool must be a reparse point (symlink), not a regular copy");

            // The symlink must resolve to the worktree's copy of pkg/bin/tool,
            // NOT the source repo's.
            var resolved = File.ResolveLinkTarget(worktreeTool, returnFinalTarget: false);
            Assert.NotNull(resolved);
            Assert.EndsWith(
                Path.Combine("deps", "pkg", "bin", "tool"),
                resolved!.FullName);
            Assert.True(resolved.FullName.StartsWith(worktree, StringComparison.Ordinal),
                "resolved target must be inside the worktree, not the source repo");

            // The resolved file has the original content.
            Assert.True(File.Exists(resolved.FullName));
            Assert.Equal("#!/bin/sh\necho ok\n", await File.ReadAllTextAsync(resolved.FullName));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 6. Cycle safety: a directory symlink that points to its parent (..)
    //    is recreated as a link node and the walk never follows or
    //    enumerates into it — overlay completes without hanging or throwing.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_CycleSymlink_DoesNotHangOrThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-cycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // deps/a/loop -> ..  (directory symlink that would cause infinite
            // recursion if followed — the walk must never descend into it).
            var aDir = Path.Combine(root, "deps", "a");
            Directory.CreateDirectory(aDir);
            Directory.CreateSymbolicLink(Path.Combine(aDir, "loop"), "..");
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-cycle", "run-cycle", CancellationToken.None, LowThresholdBytes);

            var loopLink = Path.Combine(worktree, "deps", "a", "loop");
            Assert.True(Directory.Exists(loopLink),
                "cycle directory symlink must be present in worktree");
            var attrs = File.GetAttributes(loopLink);
            Assert.True(attrs.HasFlag(FileAttributes.ReparsePoint),
                "cycle entry must be a directory reparse point (link node, not traversed)");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 7. Absolute-internal rewrite: an absolute symlink whose target is
    //    inside the source dir is rewritten to the corresponding path under
    //    the destination, keeping the snapshot self-contained.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_AbsoluteInternalSymlink_IsRewrittenToWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-absint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // A real dir with a file inside.
            var realDir = Path.Combine(root, "deps", "real");
            Directory.CreateDirectory(realDir);
            await File.WriteAllTextAsync(Path.Combine(realDir, "data.txt"), "real-data");
            // Absolute symlink that points inside the source tree.
            var absTarget = Path.GetFullPath(realDir);
            Directory.CreateSymbolicLink(Path.Combine(root, "deps", "link"), absTarget);
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-absint", "run-absint", CancellationToken.None, LowThresholdBytes);

            var linkDir = Path.Combine(worktree, "deps", "link");
            Assert.True(Directory.Exists(linkDir),
                "absolute-internal symlink must be present in worktree");
            var attrs = File.GetAttributes(linkDir);
            Assert.True(attrs.HasFlag(FileAttributes.ReparsePoint),
                "must be a directory reparse point");

            // The link target must point to the worktree's deps/real, not the source's.
            var linkTarget = new DirectoryInfo(linkDir).LinkTarget;
            Assert.NotNull(linkTarget);
            Assert.True(Path.IsPathRooted(linkTarget),
                "rewritten absolute target should remain absolute");
            Assert.True(linkTarget!.StartsWith(worktree, StringComparison.Ordinal),
                $"rewritten target must be inside the worktree ({worktree}), got: {linkTarget}");
            Assert.EndsWith(Path.Combine("deps", "real"), linkTarget);

            // The resolved directory contains the real file.
            Assert.True(File.Exists(Path.Combine(linkDir, "data.txt")));
            Assert.Equal("real-data", await File.ReadAllTextAsync(Path.Combine(linkDir, "data.txt")));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 8. Escape + teardown safety: a directory symlink pointing OUTSIDE the
    //    repo is recreated as a link node. After cleanup, the external
    //    sentinel directory and its contents are untouched — teardown never
    //    follows or traverses through the link.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupVerifyWorktree_EscapeSymlink_TargetSurvivesUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-escape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Sentinel dir OUTSIDE the repo — must survive the overlay + teardown cycle.
        var sentinelDir = Path.Combine(Path.GetTempPath(), "vr-vw-escape-sentinel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sentinelDir);
        var sentinelFile = Path.Combine(sentinelDir, "guard.txt");
        await File.WriteAllTextAsync(sentinelFile, "SENTINEL-CONTENT");
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // deps/out -> sentinel dir (directory symlink pointing OUTSIDE the repo).
            Directory.CreateDirectory(Path.Combine(root, "deps"));
            Directory.CreateSymbolicLink(Path.Combine(root, "deps", "out"), sentinelDir);
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-escape", "run-escape", CancellationToken.None, LowThresholdBytes);

            var escapeLink = Path.Combine(worktree, "deps", "out");
            Assert.True(Directory.Exists(escapeLink),
                "escape symlink must be present in worktree");
            var attrs = File.GetAttributes(escapeLink);
            Assert.True(attrs.HasFlag(FileAttributes.ReparsePoint),
                "escape entry must be a directory reparse point");

            // Resolve through the link — the sentinel file should be visible.
            Assert.True(File.Exists(Path.Combine(escapeLink, "guard.txt")));
            Assert.Equal("SENTINEL-CONTENT",
                await File.ReadAllTextAsync(Path.Combine(escapeLink, "guard.txt")));

            // NOW cleanup — must not follow the link into the sentinel.
            await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            Assert.False(Directory.Exists(worktree),
                "worktree directory should be removed after cleanup");

            // Sentinel must be untouched.
            Assert.True(Directory.Exists(sentinelDir),
                "sentinel directory outside repo must survive teardown");
            Assert.True(File.Exists(sentinelFile),
                "sentinel file must survive teardown");
            Assert.Equal("SENTINEL-CONTENT", await File.ReadAllTextAsync(sentinelFile));
        }
        finally
        {
            if (worktree is not null)
            {
                try { await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None); }
                catch { /* already cleaned up */ }
            }
            TestFileSystem.DeleteDirectoryResilient(root);
            TestFileSystem.DeleteDirectoryResilient(sentinelDir);
        }
    }
}
