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

    private static async Task RunScriptAsync(string source, string dest) =>
        await ProcessCapture.RunAsync(
            "/bin/sh",
            ["-c", WslTreeCopy.IgnoredEntriesScript, "vr-overlay", source, dest,
             (SharedThresholdBytes / 1024).ToString(), "node_modules"],
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
