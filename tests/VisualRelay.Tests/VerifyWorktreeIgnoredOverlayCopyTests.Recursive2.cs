using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed partial class VerifyWorktreeIgnoredOverlayCopyTests
{
    // ───────────────────────────────────────────────────────────────────
    // 13a. Bounded recursion fallback — max depth exceeded: a tree deeper
    //      than MaxOverlayRecursionDepth (16) triggers symlink + warn event
    //      with reason "max_depth_exceeded". Depth is deterministic.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_DeeplyNested_EmitsMaxDepthExceededWarn()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-depth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(RelayDriverDependencies.ForTests(
            new ScriptedSubagentRunner(), new ScriptedTestRunner(), sink, new GitSimEngine()));
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");

            // Build a tree deeper than MaxOverlayRecursionDepth (16).
            // depths 0..16 = 17 levels of nesting. At level 17 (>16) the
            // depth guard fires.
            var deep = root;
            for (int i = 0; i < 18; i++)
            {
                deep = Path.Combine(deep, "deps");
                Directory.CreateDirectory(deep);
            }
            await File.WriteAllTextAsync(Path.Combine(deep, "leaf.txt"), "deep");
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-depth", "run-depth", CancellationToken.None, LowThresholdBytes,
                cloneOverlay: false); // pins the recursive FALLBACK machinery

            // Worktree creation must still succeed.
            Assert.True(Directory.Exists(worktree), "worktree must be created");

            // A verify_overlay_skipped warn with reason "max_depth_exceeded".
            var depthWarn = sink.Events.FirstOrDefault(e =>
                e.Level == "warn"
                && e.EventName == "verify_overlay_skipped"
                && e.Data != null
                && e.Data.TryGetValue("reason", out var r)
                && r == "max_depth_exceeded");
            Assert.NotNull(depthWarn);
            Assert.NotNull(depthWarn!.Data);
            Assert.True(depthWarn.Data!.ContainsKey("entry"));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 13b. Bounded recursion fallback — copy budget exhausted: 7 sibling
    //      dirs each with threshold/5 bytes stay all individually below the
    //      size threshold so they are recursed into. After the fifth one the
    //      per-top-level-entry byte budget (thresholdBytes) is exhausted and
    //      the remaining two triggers a "copy_budget_exhausted" warn.
    //      Using 7 equal siblings guarantees at least one triggers the budget
    //      check regardless of enumeration order.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_BudgetExhausted_EmitsCopyBudgetExhaustedWarn()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(RelayDriverDependencies.ForTests(
            new ScriptedSubagentRunner(), new ScriptedTestRunner(), sink, new GitSimEngine()));
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");

            // 7 sibling dirs, each with threshold/5 bytes — all individually
            // below the threshold. After processing 5 dirs, the budget (threshold)
            // is exhausted; the 6th and 7th trigger the budget check. Having 2
            // dirs after the exhaustion point guarantees at least one triggers
            // regardless of enumeration order.
            var depsDir = Path.Combine(root, "deps");
            Directory.CreateDirectory(depsDir);
            var payload = new byte[LowThresholdBytes / 5];
            for (int i = 0; i < 7; i++)
            {
                var sib = Path.Combine(depsDir, $"sib{i}");
                Directory.CreateDirectory(sib);
                await File.WriteAllBytesAsync(Path.Combine(sib, "payload.bin"), payload);
            }
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-budget", "run-budget", CancellationToken.None, LowThresholdBytes,
                cloneOverlay: false); // pins the recursive FALLBACK machinery

            Assert.True(Directory.Exists(worktree), "worktree must be created");

            var budgetWarn = sink.Events.FirstOrDefault(e =>
                e.Level == "warn"
                && e.EventName == "verify_overlay_skipped"
                && e.Data != null
                && e.Data.TryGetValue("reason", out var r)
                && r == "copy_budget_exhausted");
            Assert.NotNull(budgetWarn);
            Assert.NotNull(budgetWarn!.Data);
            Assert.True(budgetWarn.Data!.ContainsKey("entry"),
                "warn event data must name the entry that was skipped");
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 14. Teardown safety at depth: a directory symlink several levels
    //     deep pointing at an outside sentinel dir survives cleanup —
    //     UnlinkOverlaySymlinks unlinks the link node without traversing
    //     into it, so the external sentinel and its contents remain intact.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupVerifyWorktree_EscapeSymlinkAtDepth_TargetSurvivesUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-escapedepth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Sentinel dir OUTSIDE the repo — must survive the overlay + teardown cycle.
        var sentinelDir = Path.Combine(Path.GetTempPath(), "vr-vw-escapedepth-sentinel-" + Guid.NewGuid().ToString("N"));
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
            // deps/a/b/c/out -> sentinelDir (directory symlink at depth 4).
            var deepDir = Path.Combine(root, "deps", "a", "b", "c");
            Directory.CreateDirectory(deepDir);
            Directory.CreateSymbolicLink(Path.Combine(deepDir, "out"), sentinelDir);
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-escapedepth", "run-escapedepth", CancellationToken.None, LowThresholdBytes);

            // The symlink at depth must be present and a reparse point.
            var escapeLink = Path.Combine(worktree, "deps", "a", "b", "c", "out");
            Assert.True(Directory.Exists(escapeLink),
                "escape symlink at depth must be present in worktree");
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
