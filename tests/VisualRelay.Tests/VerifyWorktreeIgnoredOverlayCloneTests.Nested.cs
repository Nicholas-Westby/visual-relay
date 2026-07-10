namespace VisualRelay.Tests;

/// <summary>
/// NESTED ignored-entry overlay (both the clone path and the recursive fallback):
/// a pnpm workspace's <c>packages/*/node_modules</c> must be carried into the
/// snapshot, while nested build-output dirs and VR-internal names stay excluded.
/// </summary>
public sealed partial class VerifyWorktreeIgnoredOverlayCloneTests
{
    // ───────────────────────────────────────────────────────────────────
    // 5. NESTED ignored entries — a pnpm workspace's packages/app/node_modules
    //    (ignored inside a partially-tracked parent) is overlaid: present, real
    //    content, and writes stay isolated from the source.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_NestedIgnoredDir_OverlaidRealAndWritable()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "node_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // Workspace shape: the package has tracked sources AND its own dep dir.
            Directory.CreateDirectory(Path.Combine(root, "packages", "app"));
            await File.WriteAllTextAsync(Path.Combine(root, "packages", "app", "index.ts"), "export {};");
            var nestedTypes = Path.Combine(root, "packages", "app", "node_modules", "@types", "node");
            Directory.CreateDirectory(nestedTypes);
            await File.WriteAllTextAsync(Path.Combine(nestedTypes, "index.d.ts"), "declare const x: 1;");
            await CommitAll(root, "seed"); // commits tracked + packages/app/index.ts

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-nested", "run-nested", CancellationToken.None, LowThresholdBytes);

            // The nested dep dir is overlaid (previously dropped entirely → TS2688).
            var overlaid = Path.Combine(worktree, "packages", "app", "node_modules", "@types", "node", "index.d.ts");
            Assert.True(File.Exists(overlaid),
                "nested ignored dep dir must be overlaid into the worktree");
            Assert.Equal("declare const x: 1;", await File.ReadAllTextAsync(overlaid));

            // Writes inside it stay isolated (vite writes .vite-temp in nested deps).
            var written = Path.Combine(worktree, "packages", "app", "node_modules", ".vite-temp");
            Directory.CreateDirectory(written);
            Assert.True(Directory.Exists(written));
            Assert.False(Directory.Exists(Path.Combine(root, "packages", "app", "node_modules", ".vite-temp")),
                "a write inside the nested overlay must not appear in the source repo");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 6. NESTED ignored entries — the FALLBACK (no-clone) path overlays them
    //    through the recursive machinery too; nested support must not depend
    //    on clonefile being available.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_NestedIgnoredDir_OverlaidOnFallbackPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-nestedfb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "node_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            Directory.CreateDirectory(Path.Combine(root, "packages", "app"));
            await File.WriteAllTextAsync(Path.Combine(root, "packages", "app", "index.ts"), "export {};");
            var nested = Path.Combine(root, "packages", "app", "node_modules", "dep");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "index.js"), "module.exports = 1;");
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-nestedfb", "run-nestedfb", CancellationToken.None, LowThresholdBytes,
                cloneOverlay: false);

            var overlaid = Path.Combine(worktree, "packages", "app", "node_modules", "dep", "index.js");
            Assert.True(File.Exists(overlaid),
                "nested ignored dep dir must be overlaid by the fallback machinery");
            Assert.Equal("module.exports = 1;", await File.ReadAllTextAsync(overlaid));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 7. NESTED build-output dirs stay OMITTED (path-sensitive, regenerable) —
    //    extending the overlay to nested entries must not start carrying them.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_NestedBuildOutputDir_StillOmitted()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-nestedbo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "dist/\nnode_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            Directory.CreateDirectory(Path.Combine(root, "packages", "app"));
            await File.WriteAllTextAsync(Path.Combine(root, "packages", "app", "index.ts"), "export {};");
            Directory.CreateDirectory(Path.Combine(root, "packages", "app", "dist"));
            await File.WriteAllTextAsync(Path.Combine(root, "packages", "app", "dist", "bundle.js"), "built");
            var nested = Path.Combine(root, "packages", "app", "node_modules", "dep");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "index.js"), "module.exports = 1;");
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-nestedbo", "run-nestedbo", CancellationToken.None, LowThresholdBytes);

            Assert.False(Directory.Exists(Path.Combine(worktree, "packages", "app", "dist")),
                "nested build-output dir (dist) must be omitted so the worktree builds fresh");
            Assert.True(File.Exists(Path.Combine(worktree, "packages", "app", "node_modules", "dep", "index.js")),
                "nested dependency dir must still be overlaid");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 8. VR-internal names stay EXCLUDED at any depth — the nested overlay
    //    must never carry a nested .relay/.swival into the snapshot.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_NestedVrInternalDir_NotOverlaid()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-nestedvr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".swival/\nnode_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            Directory.CreateDirectory(Path.Combine(root, "packages", "app"));
            await File.WriteAllTextAsync(Path.Combine(root, "packages", "app", "index.ts"), "export {};");
            Directory.CreateDirectory(Path.Combine(root, "packages", "app", ".swival"));
            await File.WriteAllTextAsync(Path.Combine(root, "packages", "app", ".swival", "state.json"), "{}");
            var nested = Path.Combine(root, "packages", "app", "node_modules", "dep");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "index.js"), "module.exports = 1;");
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-nestedvr", "run-nestedvr", CancellationToken.None, LowThresholdBytes);

            Assert.False(Directory.Exists(Path.Combine(worktree, "packages", "app", ".swival")),
                "nested VR-internal dir must not be overlaid");
            Assert.True(File.Exists(Path.Combine(worktree, "packages", "app", "node_modules", "dep", "index.js")),
                "nested dependency dir must still be overlaid");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
