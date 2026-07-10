using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Clonefile overlay policy for the authoritative verify worktree, plus the
/// NESTED ignored-entry overlay both paths share.
///
/// The copy/symlink threshold machinery leaves every ≥threshold directory as a
/// whole-dir symlink into the SOURCE repo, so any test-time WRITE or UNLINK that
/// resolves THROUGH that subtree lands in the real repo and the always-on nono
/// sandbox denies it (EPERM) — vitest writing tsbuildinfo inside a pnpm
/// <c>.pnpm</c> store is the mainstream case. On APFS the overlay instead CLONES
/// each ignored entry (copy-on-write, near-zero disk, seconds per GB): every
/// file is REAL and WRITABLE in the worktree, unlinks stay local, and module
/// resolution never leaves the snapshot. Where cloning is unavailable the
/// recursive copy/symlink walk remains as the fallback (exercised explicitly via
/// the seam's <c>cloneOverlay: false</c>).
///
/// Nested ignored entries (a pnpm workspace's <c>packages/*/node_modules</c>)
/// were previously DROPPED by the top-level-only enumeration, so snapshots could
/// not resolve per-package deps (zod: TS2688 "Cannot find type definition file
/// for 'node'"). They are now overlaid per entry like top-level ones; nested
/// BUILD-OUTPUT dirs stay omitted (path-sensitive) and VR-internal names stay
/// excluded at any depth.
/// </summary>
public sealed partial class VerifyWorktreeIgnoredOverlayCloneTests
{
    /// <summary>Low boundary so fixtures exercise the ≥threshold (symlink) branch
    /// of the FALLBACK machinery without writing 64 MB; the clone path must win
    /// regardless of entry size.</summary>
    private const long LowThresholdBytes = 4 * 1024; // 4 KiB

    private static RelayDriver NewDriver() =>
        new(RelayDriverDependencies.ForTests(
            new ScriptedSubagentRunner(),
            new ScriptedTestRunner(),
            new InMemoryRelayEventSink(),
            new GitSimEngine()));

    private static void InitRepo(string root) => new GitSimEngine().InitRepo(root);

    private static async Task CommitAll(string root, string message)
    {
        var sim = new GitSimEngine();
        await sim.Git(root, "add", ".");
        sim.Commit(root, message);
    }

    // ───────────────────────────────────────────────────────────────────
    // 1. CLONE PATH — an ignored dir ABOVE the threshold is materialized as a
    //    REAL directory tree (no whole-dir symlink): writes AND unlinks inside
    //    it stay in the worktree; the source repo is untouched by both.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_CloneSupported_LargeIgnoredDirIsRealWritableAndUnlinkable()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("clonefile overlay is macOS-only");
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "node_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "dep", "dist"));
            var sourceBlob = Path.Combine(root, "node_modules", "dep", "dist", "blob.bin");
            await File.WriteAllBytesAsync(sourceBlob, new byte[LowThresholdBytes * 2]);
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-clone", "run-clone", CancellationToken.None, LowThresholdBytes);

            // ABOVE-threshold dir: the fallback would symlink it whole; the clone
            // path must deliver a REAL directory with REAL file content.
            var dep = new DirectoryInfo(Path.Combine(worktree, "node_modules", "dep"));
            Assert.True(dep.Exists, "cloned dep/ should exist");
            Assert.False(dep.Attributes.HasFlag(FileAttributes.ReparsePoint),
                "cloned dep/ must be a REAL directory, not a whole-dir symlink");
            var worktreeBlob = Path.Combine(worktree, "node_modules", "dep", "dist", "blob.bin");
            Assert.False(new FileInfo(worktreeBlob).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "cloned blob must be a regular file");
            Assert.Equal(LowThresholdBytes * 2, new FileInfo(worktreeBlob).Length);

            // WRITE lands in the worktree only (vitest .vite-temp / tsbuildinfo shape).
            var written = Path.Combine(worktree, "node_modules", "dep", "dist", "tsconfig.tmp.tsbuildinfo");
            await File.WriteAllTextAsync(written, "buildinfo");
            Assert.True(File.Exists(written));
            Assert.False(File.Exists(Path.Combine(root, "node_modules", "dep", "dist", "tsconfig.tmp.tsbuildinfo")),
                "a write inside the cloned tree must not appear in the source repo");

            // UNLINK removes the worktree copy only (astro .vite-sandbox shape).
            File.Delete(worktreeBlob);
            Assert.False(File.Exists(worktreeBlob));
            Assert.True(File.Exists(sourceBlob), "unlinking the worktree clone must not delete the source file");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. CLONE PATH — pnpm layout: the ≥threshold .pnpm store becomes REAL and
    //    the package-root RELATIVE symlink entry survives the clone as a link
    //    that resolves worktree-locally.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_CloneSupported_RelativeSymlinkInsideClonedTreePreserved()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("clonefile overlay is macOS-only");
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-clonelink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "node_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // node_modules/.pnpm/pkg@1/node_modules/pkg/index.js  (real, padded ≥ threshold)
            // node_modules/pkg -> .pnpm/pkg@1/node_modules/pkg    (relative link)
            var store = Path.Combine(root, "node_modules", ".pnpm", "pkg@1", "node_modules", "pkg");
            Directory.CreateDirectory(store);
            await File.WriteAllTextAsync(Path.Combine(store, "index.js"), "module.exports = 'real';");
            await File.WriteAllBytesAsync(Path.Combine(store, "pad.bin"), new byte[LowThresholdBytes * 2]);
            File.CreateSymbolicLink(
                Path.Combine(root, "node_modules", "pkg"),
                Path.Combine(".pnpm", "pkg@1", "node_modules", "pkg"));
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-clonelink", "run-clonelink", CancellationToken.None, LowThresholdBytes);

            // The store is REAL (the fallback would leave it a whole-dir symlink).
            var pnpmDir = new DirectoryInfo(Path.Combine(worktree, "node_modules", ".pnpm"));
            Assert.False(pnpmDir.Attributes.HasFlag(FileAttributes.ReparsePoint),
                ".pnpm store must be a REAL directory after clone");

            // The package-root entry is STILL a symlink, still relative, and
            // resolves to the WORKTREE's store — never back into the source.
            var pkgLink = new DirectoryInfo(Path.Combine(worktree, "node_modules", "pkg"));
            Assert.True(pkgLink.Attributes.HasFlag(FileAttributes.ReparsePoint),
                "package-root symlink entry must be preserved as a link");
            Assert.False(Path.IsPathRooted(pkgLink.LinkTarget),
                "relative link target must stay relative");
            Assert.Equal("module.exports = 'real';",
                await File.ReadAllTextAsync(Path.Combine(worktree, "node_modules", "pkg", "index.js")));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. CLONE PATH — an ABSOLUTE symlink pointing INSIDE the source repo is
    //    rewritten to the worktree (clonefile copies link targets verbatim, so
    //    without the rewrite a write through it would escape the sandbox cwd).
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_CloneSupported_AbsoluteInternalSymlinkRewrittenToWorktree()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("clonefile overlay is macOS-only");
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-cloneabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            var real = Path.Combine(root, "deps", "real");
            Directory.CreateDirectory(real);
            await File.WriteAllTextAsync(Path.Combine(real, "lib.js"), "lib");
            await File.WriteAllBytesAsync(Path.Combine(real, "pad.bin"), new byte[LowThresholdBytes * 2]);
            // ABSOLUTE internal link (venv-style wiring).
            File.CreateSymbolicLink(Path.Combine(root, "deps", "alias"), real);
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-cloneabs", "run-cloneabs", CancellationToken.None, LowThresholdBytes);

            // The link's TARGET dir is materialized real by the clone (the fallback
            // would leave the ≥threshold real/ a whole-dir symlink into the source).
            Assert.False(new DirectoryInfo(Path.Combine(worktree, "deps", "real")).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "cloned deps/real must be a REAL directory");
            var alias = new DirectoryInfo(Path.Combine(worktree, "deps", "alias"));
            Assert.True(alias.Attributes.HasFlag(FileAttributes.ReparsePoint),
                "absolute internal symlink entry must remain a link");
            var target = alias.LinkTarget!;
            Assert.True(Path.IsPathRooted(target));
            var resolvedWorktree = Path.GetFullPath(worktree);
            Assert.True(
                Path.GetFullPath(target).StartsWith(resolvedWorktree + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "absolute internal link target must be rewritten from the source repo to the worktree");
            Assert.Equal("lib", await File.ReadAllTextAsync(Path.Combine(worktree, "deps", "alias", "lib.js")));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
