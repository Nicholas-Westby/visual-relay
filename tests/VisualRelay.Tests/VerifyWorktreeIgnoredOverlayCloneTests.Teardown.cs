namespace VisualRelay.Tests;

/// <summary>
/// Teardown safety for CLONED overlay trees: clones are real files plus verbatim
/// link nodes, and cleanup must keep its never-follow-a-symlink guarantee.
/// </summary>
public sealed partial class VerifyWorktreeIgnoredOverlayCloneTests
{
    // ───────────────────────────────────────────────────────────────────
    // 4. CLONE PATH TEARDOWN — a cloned tree containing an absolute symlink to
    //    an OUTSIDE sentinel dir: create→cleanup must remove the worktree while
    //    the sentinel (and the source tree) survive untouched.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupVerifyWorktree_ClonedTreeEscapeSymlink_OutsideSentinelSurvives()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("clonefile overlay is macOS-only");
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-clonesafe-" + Guid.NewGuid().ToString("N"));
        var sentinel = Path.Combine(Path.GetTempPath(), "vr-vw-clonesafe-sentinel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sentinel);
        var driver = NewDriver();
        try
        {
            var sentinelFile = Path.Combine(sentinel, "keep.txt");
            await File.WriteAllTextAsync(sentinelFile, "KEEP");
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            Directory.CreateDirectory(Path.Combine(root, "deps"));
            var sourceBlob = Path.Combine(root, "deps", "blob.bin");
            await File.WriteAllBytesAsync(sourceBlob, new byte[LowThresholdBytes * 2]);
            // deps/out -> OUTSIDE sentinel dir (absolute escape link inside the clone).
            File.CreateSymbolicLink(Path.Combine(root, "deps", "out"), sentinel);
            await CommitAll(root, "seed");

            var worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-clonesafe", "run-clonesafe", CancellationToken.None, LowThresholdBytes);

            // Sanity: the clone materialized deps/ as a real tree (the fallback would
            // leave the ≥threshold blob a file symlink) with the escape link intact.
            Assert.False(new DirectoryInfo(Path.Combine(worktree, "deps")).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "cloned deps/ must be a REAL directory");
            Assert.False(new FileInfo(Path.Combine(worktree, "deps", "blob.bin")).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "cloned blob.bin must be a regular file, not a file symlink");
            Assert.True(new DirectoryInfo(Path.Combine(worktree, "deps", "out")).Attributes.HasFlag(FileAttributes.ReparsePoint));

            await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);

            Assert.False(Directory.Exists(worktree), "worktree directory should be removed");
            Assert.True(Directory.Exists(sentinel), "sentinel directory outside repo must survive teardown");
            Assert.Equal("KEEP", await File.ReadAllTextAsync(sentinelFile));
            Assert.True(File.Exists(sourceBlob), "source deps content must survive teardown");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
            TestFileSystem.DeleteDirectoryResilient(sentinel);
        }
    }
}
