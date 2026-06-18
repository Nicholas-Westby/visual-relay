using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <see cref="NonoRollbackSkipDirs"/> — the computation of nono
/// <c>--skip-dir</c> names that keep the rollback PREFLIGHT under nono's fixed
/// budget on large target repos without removing swival's read/write access.
/// </summary>
public sealed class NonoRollbackSkipDirsTests
{
    // ── Pure decision logic (no real git / no real filesystem) ──────────

    [Fact]
    public void Decide_AlwaysIncludesVcsAndVrArtifactDirs()
    {
        // With NO ignored dirs at all, the result is exactly the always-list:
        // VCS/VR-internal artifact dirs that are never rollback-relevant.
        var result = NonoRollbackSkipDirs.Decide(
            ignoredTopLevelDirs: Array.Empty<string>(),
            dirMeetsSizeThreshold: _ => true);

        Assert.Contains(".git", result);
        Assert.Contains(".relay", result);
        Assert.Contains(".relay-scratch", result);
        Assert.Contains(".swival", result);
    }

    [Fact]
    public void Decide_IncludesIgnoredDirsThatMeetSize_ExcludesThoseThatDont()
    {
        // node_modules is ignored AND large → included.
        // cache is ignored but small → excluded.
        var ignored = new[] { "node_modules", "cache" };

        var result = NonoRollbackSkipDirs.Decide(
            ignored,
            dirMeetsSizeThreshold: name => name == "node_modules");

        Assert.Contains("node_modules", result);
        Assert.DoesNotContain("cache", result);
    }

    [Fact]
    public void Decide_ExcludesLargeDirThatIsNotIgnored_TheGate()
    {
        // src is a fully-tracked large source dir. It is NOT in the ignored
        // set, so even though the size predicate would say "yes" for it, it
        // must NOT be skipped (its rollback protection is preserved).
        var ignored = new[] { "data" };

        var result = NonoRollbackSkipDirs.Decide(
            ignored,
            dirMeetsSizeThreshold: _ => true); // every dir "meets" size

        Assert.Contains("data", result);          // ignored + large → in
        Assert.DoesNotContain("src", result);     // large but NOT ignored → out (the gate)
    }

    [Fact]
    public void Decide_DeduplicatesNamesOrdinal()
    {
        // ".git" appears in the always-list AND is reported as an ignored,
        // size-meeting dir. It must appear exactly once.
        var ignored = new[] { ".git", "data" };

        var result = NonoRollbackSkipDirs.Decide(
            ignored,
            dirMeetsSizeThreshold: _ => true);

        Assert.Equal(1, result.Count(n => n == ".git"));
        Assert.Equal(result.Count, result.Distinct(StringComparer.Ordinal).Count());
    }

    // ── Size early-exit helper ──────────────────────────────────────────

    [Fact]
    public void DirectoryMeetsSizeThreshold_UnderThreshold_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vr-skipdir-small-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), new string('x', 100));
            File.WriteAllText(Path.Combine(dir, "b.txt"), new string('y', 100));

            // 200 bytes total, threshold 10_000 → false.
            Assert.False(NonoRollbackSkipDirs.DirectoryMeetsSizeThreshold(dir, 10_000));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DirectoryMeetsSizeThreshold_OverThreshold_ReturnsTrue_WithSmallInjectedThreshold()
    {
        // Use a TINY injected threshold so we never write 256 MB — the early
        // exit must fire well before fully sizing the tree.
        var dir = Path.Combine(Path.GetTempPath(), "vr-skipdir-big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), new string('x', 5_000));
            File.WriteAllText(Path.Combine(dir, "nested", "b.txt"), new string('y', 5_000));

            // 10_000 bytes total, threshold 1_000 → true.
            Assert.True(NonoRollbackSkipDirs.DirectoryMeetsSizeThreshold(dir, 1_000));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DirectoryMeetsSizeThreshold_MissingDir_ReturnsFalse_NeverThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vr-skipdir-absent-" + Guid.NewGuid().ToString("N"));
        Assert.False(NonoRollbackSkipDirs.DirectoryMeetsSizeThreshold(dir, 1));
    }

    // ── Real-git integration (real GitInvoker + temp `git init`) ────────

    [Fact]
    public async Task ComputeAsync_RealRepo_IncludesAlwaysList_AndIgnoredLargeDir_ExcludesTrackedDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-skipdir-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestGit.Run(root, "init", "-q");

            // Tracked source dir (large but fully tracked → must NOT be skipped).
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "a.cs"), new string('a', 4_000));
            File.WriteAllText(Path.Combine(root, "src", "b.cs"), new string('b', 4_000));

            // gitignored large dir → must be skipped (once over threshold).
            File.WriteAllText(Path.Combine(root, ".gitignore"), "big/\n");
            Directory.CreateDirectory(Path.Combine(root, "big"));
            File.WriteAllText(Path.Combine(root, "big", "blob.bin"), new string('z', 8_000));

            // A VR-internal artifact dir that exists on disk.
            Directory.CreateDirectory(Path.Combine(root, ".relay"));
            File.WriteAllText(Path.Combine(root, ".relay", "state.json"), "{}");

            TestGit.Run(root, "add", ".gitignore", "src/a.cs", "src/b.cs");

            var invoker = new GitInvoker("/usr/bin/git");

            // Lowered threshold seam so we don't write 256 MB but still exercise the gate.
            var result = await NonoRollbackSkipDirs.ComputeAsync(
                root, invoker, CancellationToken.None, thresholdBytes: 1_000);

            // Always-list members present.
            Assert.Contains(".git", result);
            Assert.Contains(".relay", result);
            Assert.Contains(".relay-scratch", result);
            Assert.Contains(".swival", result);

            // gitignored + large → included.
            Assert.Contains("big", result);

            // fully-tracked source dir → excluded (the gate).
            Assert.DoesNotContain("src", result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeAsync_NullGitInvoker_DefaultsToRealGit_AndStillSizeGatesIgnoredDirs()
    {
        // Regression guard for the production wiring gap: SwivalSubagentRunner was
        // constructed WITHOUT a gitInvoker at every production site, which silently
        // dropped the size-gated skips (keeping only the always-list) and let a
        // multi-GB git-ignored dir blow nono's rollback budget. A null invoker MUST
        // default to a real GitInvoker so big git-ignored dirs are still skipped.
        var root = Path.Combine(Path.GetTempPath(), "vr-skipdir-nullgit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestGit.Run(root, "init", "-q");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "big/\n");
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "x");
            Directory.CreateDirectory(Path.Combine(root, "big"));
            File.WriteAllText(Path.Combine(root, "big", "blob.bin"), new string('z', 8_000));
            TestGit.Run(root, "add", ".gitignore", "tracked.txt");

            // gitInvoker: null → must fall back to a REAL GitInvoker (not the
            // always-list only), so the ignored "big" dir is still size-gated in.
            var result = await NonoRollbackSkipDirs.ComputeAsync(
                root, gitInvoker: null, CancellationToken.None, thresholdBytes: 1_000);

            Assert.Contains(".git", result);
            Assert.Contains("big", result); // proves real git ran despite the null invoker
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
