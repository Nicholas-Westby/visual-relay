using System.Runtime.Versioning;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The verify snapshot's copies on the Windows arm run inside the distro. Measured in WSL
/// with FreshRSS: files the app copied through the <c>\\wsl.localhost</c> share were created
/// 0644, so Verify's <c>vendor/bin/phpunit</c> failed with "Permission denied" (exit 126)
/// while Author-tests had run it in the workspace. These run the real scripts under
/// <c>/bin/sh</c>, which is what the distro runs; POSIX tools only.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class WslTreeCopyScriptTests : IDisposable
{
    /// <summary>The production free-copy size, in the KiB the script takes it in.</summary>
    private static string FreeKbFor(string limitKb) =>
        (WslTreeCopy.FreeBytesFor(long.Parse(limitKb) * 1024) / 1024)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    private readonly string _root = Directory.CreateTempSubdirectory("vr-treecopy-").FullName;
    private string Source => Path.Combine(_root, "src");
    private string Dest => Path.Combine(_root, "dst");

    public WslTreeCopyScriptTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Dest);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task IgnoredEntries_SmallDirectory_IsCopiedWithItsExecuteBits()
    {
        SkipOnWindows();
        var phpunit = Write("vendor/bin/phpunit", "#!/bin/sh\necho ok\n");
        File.SetUnixFileMode(phpunit, (UnixFileMode)0b111_101_101);

        var failed = await RunAsync(WslTreeCopy.IgnoredEntriesScript, "1024", "vendor");

        Assert.Empty(failed);
        var copied = Path.Combine(Dest, "vendor", "bin", "phpunit");
        Assert.False(new FileInfo(Path.Combine(Dest, "vendor")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.True(File.GetUnixFileMode(copied).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task IgnoredEntries_LargeDirectory_IsARealDirectoryOfLinkedAndCopiedChildren()
    {
        SkipOnWindows();
        WriteLargeTree();

        var failed = await RunAsync(WslTreeCopy.IgnoredEntriesScript, LimitKb, "node_modules");

        Assert.Empty(failed);
        var dir = Path.Combine(Dest, "node_modules");
        Assert.False(IsLink(dir));
        Assert.Equal(Path.Combine(Source, "node_modules", "big"),
            new DirectoryInfo(Path.Combine(dir, "big")).LinkTarget);
        Assert.False(IsLink(Path.Combine(dir, "small")));
        Assert.Equal("small", File.ReadAllText(Path.Combine(dir, "small", "index.js")));
        Assert.False(IsLink(Path.Combine(dir, ".bin")));
        Assert.Equal("#!/bin/sh\n", File.ReadAllText(Path.Combine(dir, ".bin", "tool")));
    }

    /// <summary>
    /// The whole-folder link made every path under it the checkout's, and the sandbox mounts the
    /// checkout read-only: on i18next vitest could not create <c>node_modules/.vite-temp</c>,
    /// printed EACCES and exited before a test ran.
    /// </summary>
    [Fact]
    public async Task IgnoredEntries_AFileCreatedInTheSnapshotsFolder_DoesNotReachTheSource()
    {
        SkipOnWindows();
        WriteLargeTree();

        await RunAsync(WslTreeCopy.IgnoredEntriesScript, LimitKb, "node_modules");
        File.WriteAllText(Path.Combine(Dest, "node_modules", ".vite-temp"), "probe");

        Assert.False(File.Exists(Path.Combine(Source, "node_modules", ".vite-temp")));
    }

    [Fact]
    public async Task IgnoredEntries_TheCopyBudget_LinksWhatIsLeft()
    {
        SkipOnWindows();
        // Three 40 KiB children against a 64 KiB budget: the third is reached with it spent.
        foreach (var name in (string[])["a.bin", "b.bin", "c.bin"])
            Write($"deps/{name}", new string('x', 40 * 1024));

        var failed = await RunAsync(WslTreeCopy.IgnoredEntriesScript, LimitKb, "deps");

        Assert.Empty(failed);
        Assert.False(IsLink(Path.Combine(Dest, "deps", "a.bin")));
        Assert.False(IsLink(Path.Combine(Dest, "deps", "b.bin")));
        Assert.True(IsLink(Path.Combine(Dest, "deps", "c.bin")));
    }

    /// <summary>
    /// The teardown deletes the snapshot, and the links inside the new real folder point at the
    /// checkout, so a delete that followed one would empty the checkout's dependencies.
    /// </summary>
    [Fact]
    public async Task IgnoredEntries_Cleanup_LeavesTheSourceFolderIntact()
    {
        SkipOnWindows();
        WriteLargeTree();

        await RunAsync(WslTreeCopy.IgnoredEntriesScript, LimitKb, "node_modules");
        WorktreeLinks.UnlinkAll(Dest);
        Directory.Delete(Dest, recursive: true);

        Assert.Equal(256 * 1024, new FileInfo(Path.Combine(Source, "node_modules", "big", "blob.bin")).Length);
        Assert.Equal("small", File.ReadAllText(Path.Combine(Source, "node_modules", "small", "index.js")));
        Assert.Equal("#!/bin/sh\n", File.ReadAllText(Path.Combine(Source, "node_modules", ".bin", "tool")));
    }

    [Fact]
    public async Task IgnoredEntries_NestedEntry_AndAnExistingDestination()
    {
        SkipOnWindows();
        Write("packages/a/node_modules/x.js", "x");
        Write(".env", "SECRET=from-source");
        File.WriteAllText(Path.Combine(Dest, ".env"), "kept");

        var failed = await RunAsync(WslTreeCopy.IgnoredEntriesScript, "1024", "packages/a/node_modules", ".env");

        Assert.Empty(failed);
        Assert.True(File.Exists(Path.Combine(Dest, "packages", "a", "node_modules", "x.js")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(Dest, ".env")));
    }

    [Fact]
    public async Task Files_AreCopiedOverTheCheckoutWithTheirModes_AndAMissingOneIsReported()
    {
        SkipOnWindows();
        var script = Write("tools/run.sh", "#!/bin/sh\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_101_101);
        Directory.CreateDirectory(Path.Combine(Dest, "tools"));
        File.WriteAllText(Path.Combine(Dest, "tools", "run.sh"), "old checkout");

        var failed = await RunAsync(WslTreeCopy.FilesScript, null, "tools/run.sh", "gone.txt");

        Assert.Equal(["gone.txt"], failed);
        Assert.Equal("#!/bin/sh\n", File.ReadAllText(Path.Combine(Dest, "tools", "run.sh")));
        Assert.True(File.GetUnixFileMode(Path.Combine(Dest, "tools", "run.sh")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Launch_RunsTheScriptInTheDistroWithEveryNameAsItsOwnArgument()
    {
        var context = new WslContext(@"C:\Windows\System32\wsl.exe", "VrNoSuchDistro", "/usr/bin/nono", "/home/u");

        var launch = WslTreeCopy.IgnoredEntries(context, "/home/u/repo", "/home/u/.cache/wt/x", 64L * 1024 * 1024, ["a b", "c"]);

        Assert.Equal(context.WslExePath, launch.FileName);
        Assert.Equal(
            ["-d", "VrNoSuchDistro", "--exec", "/bin/sh", "-c", WslTreeCopy.IgnoredEntriesScript, "vr-overlay",
             "/home/u/repo", "/home/u/.cache/wt/x", "65536", "256", "a b", "c"],
            launch.Arguments);
    }

    [Fact]
    public void InDistro_BothPathsInTheResolvedDistro_AreTheirLinuxPaths()
    {
        var context = new WslContext(@"C:\Windows\System32\wsl.exe", "VrNoSuchDistro", "/usr/bin/nono", "/home/u");

        var paths = WslTreeCopy.InDistro(SandboxHost.Windows(context),
            @"\\wsl.localhost\VrNoSuchDistro\home\u\FreshRSS", @"\\wsl.localhost\VrNoSuchDistro\home\u\.cache\visual-relay\wt\x");

        Assert.Equal((context, "/home/u/FreshRSS", "/home/u/.cache/visual-relay/wt/x"), paths);
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\VrNoSuchOtherDistro\home\u\repo", @"\\wsl.localhost\VrNoSuchDistro\home\u\wt")]
    [InlineData(@"C:\repo", @"\\wsl.localhost\VrNoSuchDistro\home\u\wt")]
    public void InDistro_APathOutsideTheResolvedDistro_KeepsTheAppSideCopy(string source, string dest)
    {
        var context = new WslContext(@"C:\Windows\System32\wsl.exe", "VrNoSuchDistro", "/usr/bin/nono", "/home/u");

        Assert.Null(WslTreeCopy.InDistro(SandboxHost.Windows(context), source, dest));
        Assert.Null(WslTreeCopy.InDistro(SandboxHost.Local, "/home/u/repo", "/home/u/wt"));
    }

    /// <summary>
    /// The i18next shape, and the defect it exposed. Its node_modules has 395 children;
    /// the copy budget was spent by the first fifteen alphabetically, and the script's
    /// three glob patterns put every dot-named entry last, so the 4 KB `.vite-temp`
    /// scratch directory was linked back to the real checkout no matter how small it was.
    /// vitest then wrote its config timestamp through that link, into a checkout verify
    /// mounts read-only, and got EACCES before a test ran. Proven on the machine by
    /// running this very script and writing a file through the result.
    /// </summary>
    [Fact]
    public async Task IgnoredEntries_ABudgetSpentByEarlierSiblings_StillCopiesATinyScratchDir()
    {
        SkipOnWindows();
        // Enough ordinary children ahead of it, alphabetically, to exhaust the budget.
        for (var i = 0; i < 4; i++)
            Write($"node_modules/@pkg{i}/blob.bin", new string('x', 1024 * 1024));
        // Real bytes: an EMPTY dir measures 0 KB, and "0 is at or below the free size"
        // is true for every free size including none, so an empty fixture would pass
        // against the very defect this covers.
        Write("node_modules/.vite-temp/cache.json", new string('x', 8 * 1024));

        var failed = await RunAsync(WslTreeCopy.IgnoredEntriesScript, BigLimitKb, "node_modules");

        Assert.Empty(failed);
        var scratch = Path.Combine(Dest, "node_modules", ".vite-temp");
        Assert.True(Directory.Exists(scratch));
        Assert.False(IsLink(scratch), "a tiny scratch dir must not be linked back to the checkout");

        // The write that failed on i18next, and where it lands.
        await File.WriteAllTextAsync(Path.Combine(scratch, "timestamp.mjs"), "x");
        Assert.False(File.Exists(Path.Combine(Source, "node_modules", ".vite-temp", "timestamp.mjs")));
    }

    /// <summary>The budget still holds: a child too big to be free is linked once it is spent.</summary>
    [Fact]
    public async Task IgnoredEntries_ABudgetSpentByEarlierSiblings_StillLinksABigLateChild()
    {
        SkipOnWindows();
        for (var i = 0; i < 4; i++)
            Write($"node_modules/@pkg{i}/blob.bin", new string('x', 1024 * 1024));
        Write("node_modules/zlate/blob.bin", new string('x', 1024 * 1024));

        var failed = await RunAsync(WslTreeCopy.IgnoredEntriesScript, BigLimitKb, "node_modules");

        Assert.Empty(failed);
        Assert.True(IsLink(Path.Combine(Dest, "node_modules", "zlate")));
    }

    private static void SkipOnWindows() =>
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the scripts run inside the distro; /bin/sh stands in for it");

    /// <summary>A limit coarse enough that <c>du</c>'s block rounding cannot decide an entry's side of it.</summary>
    private const string LimitKb = "64";

    /// <summary>A limit big enough that the free size is the production one (256 KiB).</summary>
    private const string BigLimitKb = "4096";

    private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>A folder above the limit holding a child above it, one below it and a dot-named one.</summary>
    private void WriteLargeTree()
    {
        Write("node_modules/big/blob.bin", new string('x', 256 * 1024));
        Write("node_modules/small/index.js", "small");
        Write("node_modules/.bin/tool", "#!/bin/sh\n");
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<IReadOnlyList<string>> RunAsync(string script, string? limitKb, params string[] names)
    {
        string[] args = limitKb is null
            ? ["-c", script, "vr-overlay", Source, Dest, .. names]
            : ["-c", script, "vr-overlay", Source, Dest, limitKb, FreeKbFor(limitKb), .. names];
        var (_, output, _) = await ProcessCapture.RunAsync("/bin/sh", args, _root, TimeSpan.FromSeconds(60), CancellationToken.None);
        return WslTreeCopy.FailedNames(output);
    }
}
