using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed partial class VerifyWorktreeIgnoredOverlayCopyTests
{
    // ───────────────────────────────────────────────────────────────────
    // 11. Writable dep root (the axios/zod case): a git-ignored dir whose
    //     large children are symlinked but the root itself is a REAL,
    //     writable dir — a test runner writing a new path inside the dep
    //     root succeeds in the sandbox and never touches the source.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_DepRootWithLargeChildren_IsRealDir_WritesStayIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-deproot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // deps/big/ exceeds the injected threshold → whole-dir symlink.
            Directory.CreateDirectory(Path.Combine(root, "deps", "big"));
            await File.WriteAllBytesAsync(
                Path.Combine(root, "deps", "big", "blob.bin"),
                new byte[LowThresholdBytes * 2]);
            // deps/small/ is below threshold → real dir, recursed into.
            Directory.CreateDirectory(Path.Combine(root, "deps", "small", "nested"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "deps", "small", "nested", "seed.txt"), "seed");
            // deps/meta.txt is a small file → copied.
            await File.WriteAllTextAsync(Path.Combine(root, "deps", "meta.txt"), "meta");
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-deproot", "run-deproot", CancellationToken.None, LowThresholdBytes,
                cloneOverlay: false); // pins the recursive FALLBACK machinery

            // deps/ itself must be a REAL directory — NOT a reparse point.
            var depsDir = Path.Combine(worktree, "deps");
            Assert.True(Directory.Exists(depsDir), "deps/ should be present in worktree");
            Assert.False(new DirectoryInfo(depsDir).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "dep root must be a REAL directory (writable), not a whole-dir symlink");

            // deps/big/ is a directory symlink (large child → shared via link).
            var bigDir = Path.Combine(worktree, "deps", "big");
            Assert.True(Directory.Exists(bigDir), "deps/big/ should be present");
            Assert.True(new DirectoryInfo(bigDir).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "deps/big/ must be a directory symlink (above threshold)");

            // deps/small/… and deps/meta.txt are real copies.
            var smallDir = Path.Combine(worktree, "deps", "small");
            Assert.True(Directory.Exists(smallDir));
            Assert.False(new DirectoryInfo(smallDir).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "deps/small/ must be a real directory (below threshold)");
            Assert.True(File.Exists(Path.Combine(smallDir, "nested", "seed.txt")));
            Assert.Equal("seed",
                await File.ReadAllTextAsync(Path.Combine(smallDir, "nested", "seed.txt")));

            var metaFile = Path.Combine(worktree, "deps", "meta.txt");
            Assert.True(File.Exists(metaFile));
            Assert.False(new FileInfo(metaFile).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "deps/meta.txt must be a real copied file");
            Assert.Equal("meta", await File.ReadAllTextAsync(metaFile));

            // WRITE-ISOLATION: creating a new file inside the writable deps/
            // root succeeds and does NOT appear in the source repo.
            var newCacheDir = Path.Combine(worktree, "deps", "newcache");
            Directory.CreateDirectory(newCacheDir);
            var newFile = Path.Combine(newCacheDir, "x");
            await File.WriteAllTextAsync(newFile, "from-test");
            Assert.True(File.Exists(newFile), "write inside deps/ must succeed");

            Assert.False(Directory.Exists(Path.Combine(root, "deps", "newcache")),
                "new file written in worktree deps/ must NOT appear in source repo deps/");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 12. Nested link preservation: a relative symlink several levels
    //     deep inside a small subdir survives the recursive walk and
    //     resolves to the worktree copy, not the source.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_NestedRelativeSymlink_IsPreservedAndResolvesInsideWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-nestedlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // Real file inside a package dir (at depth inside deps/small/).
            var pkgToolDir = Path.Combine(root, "deps", "small", "pkg");
            Directory.CreateDirectory(pkgToolDir);
            await File.WriteAllTextAsync(Path.Combine(pkgToolDir, "tool"), "#!/bin/sh\necho ok\n");
            // Relative symlink: deps/small/.bin/tool -> ../pkg/tool
            var dotBin = Path.Combine(root, "deps", "small", ".bin");
            Directory.CreateDirectory(dotBin);
            File.CreateSymbolicLink(Path.Combine(dotBin, "tool"), "../pkg/tool");
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-nestedlink", "run-nestedlink", CancellationToken.None, LowThresholdBytes);

            var worktreeTool = Path.Combine(worktree, "deps", "small", ".bin", "tool");
            Assert.True(File.Exists(worktreeTool),
                "nested relative symlink must be present in worktree");
            var attrs = File.GetAttributes(worktreeTool);
            Assert.True(attrs.HasFlag(FileAttributes.ReparsePoint),
                ".bin/tool must be a reparse point (symlink), not a regular copy");

            // The symlink must resolve to the worktree's copy, not the source repo.
            var resolved = File.ResolveLinkTarget(worktreeTool, returnFinalTarget: false);
            Assert.NotNull(resolved);
            Assert.EndsWith(
                Path.Combine("deps", "small", "pkg", "tool"),
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

}
