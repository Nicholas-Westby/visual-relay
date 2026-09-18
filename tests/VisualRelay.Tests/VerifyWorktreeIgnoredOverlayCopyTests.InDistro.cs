using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

public sealed partial class VerifyWorktreeIgnoredOverlayCopyTests
{
    /// <summary>A limit coarse enough that <c>du</c>'s block rounding cannot decide an entry's side of it.</summary>
    private const long SharedThresholdBytes = 64 * 1024;

    /// <summary>
    /// One rule on both arms. The app walks the checkout on the Mac and a script inside the distro
    /// does it on Windows, and while the script linked a large folder whole the two snapshots were
    /// writable in different places: on i18next vitest could not create a file under the linked
    /// node_modules and exited before a test ran.
    /// </summary>
    [Fact]
    public async Task IgnoredOverlay_TheAppWalkAndTheInDistroScript_LayTheSameTreeOut()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the script runs inside the distro; /bin/sh stands in for it");
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-botharms-" + Guid.NewGuid().ToString("N"));
        var checkout = Path.Combine(root, "checkout");
        var source = Path.Combine(root, "source");
        var script = Path.Combine(root, "script");
        Directory.CreateDirectory(checkout);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(checkout);
            await File.WriteAllTextAsync(Path.Combine(checkout, ".gitignore"), "node_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(checkout, "tracked.txt"), "tracked");
            WriteFixture(Path.Combine(checkout, "node_modules"));
            await CommitAll(checkout, "seed");
            WriteFixture(Path.Combine(source, "node_modules"));
            Directory.CreateDirectory(script);

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                checkout, "task-botharms", "run-botharms", CancellationToken.None,
                SharedThresholdBytes, cloneOverlay: false);
            await RunScriptAsync(source, script);

            Assert.Equal(
                Kinds(Path.Combine(worktree, "node_modules")),
                Kinds(Path.Combine(script, "node_modules")));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(checkout, worktree);
            WorktreeLinks.UnlinkAll(script);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// The same rule, on a fixture where ORDER decides the answer. The test above cannot
    /// see order: its four children come out the same whichever way the arm walks them.
    /// So the arms diverged unnoticed — the script's globs are sorted by the shell while
    /// the app walked filesystem enumeration order, which on APFS is a hash. Same
    /// checkout, same threshold, a different set of children copied, and not reproducible
    /// across machines. Eight equal children against a budget that stops after four make
    /// the copied set depend entirely on the order, so the two arms must agree on it.
    /// </summary>
    [Fact]
    public async Task IgnoredOverlay_WhenTheBudgetStopsPartWay_BothArmsCopyTheSameChildren()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the script runs inside the distro; /bin/sh stands in for it");
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-order-" + Guid.NewGuid().ToString("N"));
        var checkout = Path.Combine(root, "checkout");
        var source = Path.Combine(root, "source");
        var script = Path.Combine(root, "script");
        Directory.CreateDirectory(checkout);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(checkout);
            await File.WriteAllTextAsync(Path.Combine(checkout, ".gitignore"), "node_modules/\n");
            await File.WriteAllTextAsync(Path.Combine(checkout, "tracked.txt"), "tracked");
            WriteOrderFixture(Path.Combine(checkout, "node_modules"));
            await CommitAll(checkout, "seed");
            WriteOrderFixture(Path.Combine(source, "node_modules"));
            Directory.CreateDirectory(script);

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                checkout, "task-order", "run-order", CancellationToken.None,
                OrderThresholdBytes, cloneOverlay: false);
            await RunScriptAsync(source, script, OrderThresholdBytes);

            var appKinds = Kinds(Path.Combine(worktree, "node_modules"));
            // The fixture only proves anything while the budget really does stop part way.
            Assert.Contains("=link", appKinds, StringComparison.Ordinal);
            Assert.Contains("=dir", appKinds, StringComparison.Ordinal);
            Assert.Equal(appKinds, Kinds(Path.Combine(script, "node_modules")));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(checkout, worktree);
            WorktreeLinks.UnlinkAll(script);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>Big enough that du's block rounding cannot move a child across the budget.</summary>
    private const long OrderThresholdBytes = 1024 * 1024;

    /// <summary>
    /// Eight equal children of 300 KiB against a 1 MiB budget: four fit and four do not,
    /// and which four is decided purely by the order the arm walks them in. Each is well
    /// above the free size (64 KiB here) so they all compete for the same budget.
    /// </summary>
    private static void WriteOrderFixture(string dir)
    {
        for (var i = 1; i <= 8; i++)
        {
            Directory.CreateDirectory(Path.Combine(dir, $"d{i}"));
            File.WriteAllBytes(Path.Combine(dir, $"d{i}", "blob.bin"), new byte[300 * 1024]);
        }
    }

    /// <summary>A folder above the limit holding a child above it, one below it, a dot-named one and a file.</summary>
    private static void WriteFixture(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "big"));
        File.WriteAllBytes(Path.Combine(dir, "big", "blob.bin"), new byte[SharedThresholdBytes * 4]);
        Directory.CreateDirectory(Path.Combine(dir, "small", "nested"));
        File.WriteAllText(Path.Combine(dir, "small", "nested", "seed.txt"), "seed");
        Directory.CreateDirectory(Path.Combine(dir, ".bin"));
        File.WriteAllText(Path.Combine(dir, ".bin", "tool"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(dir, "meta.txt"), "meta");
    }

    private static async Task RunScriptAsync(string source, string dest, long threshold = SharedThresholdBytes) =>
        await ProcessCapture.RunAsync(
            "/bin/sh",
            ["-c", WslTreeCopy.IgnoredEntriesScript, "vr-overlay", source, dest,
             (threshold / 1024).ToString(),
             (WslTreeCopy.FreeBytesFor(threshold) / 1024).ToString(), "node_modules"],
            dest, TimeSpan.FromSeconds(60), CancellationToken.None);

    /// <summary>What each child of <paramref name="dir"/> is: a link, a real directory or a file.</summary>
    private static string Kinds(string dir) =>
        string.Join(", ", Directory.EnumerateFileSystemEntries(dir)
            .Select(path => (Name: Path.GetFileName(path)!, Kind:
                File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) ? "link"
                : Directory.Exists(path) ? "dir" : "file"))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => $"{entry.Name}={entry.Kind}"));
}
