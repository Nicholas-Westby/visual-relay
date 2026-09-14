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
    public async Task IgnoredEntries_LargeDirectory_IsLinkedToTheSource()
    {
        SkipOnWindows();
        Write("node_modules/big.bin", new string('x', 64 * 1024));

        var failed = await RunAsync(WslTreeCopy.IgnoredEntriesScript, "16", "node_modules");

        Assert.Empty(failed);
        var link = new DirectoryInfo(Path.Combine(Dest, "node_modules"));
        Assert.Equal(Path.Combine(Source, "node_modules"), link.LinkTarget);
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
        var context = new WslContext(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/bin/nono", "/home/u");

        var launch = WslTreeCopy.IgnoredEntries(context, "/home/u/repo", "/home/u/.cache/wt/x", 64L * 1024 * 1024, ["a b", "c"]);

        Assert.Equal(context.WslExePath, launch.FileName);
        Assert.Equal(
            ["-d", "Ubuntu", "--exec", "/bin/sh", "-c", WslTreeCopy.IgnoredEntriesScript, "vr-overlay",
             "/home/u/repo", "/home/u/.cache/wt/x", "65536", "a b", "c"],
            launch.Arguments);
    }

    [Fact]
    public void InDistro_BothPathsInTheResolvedDistro_AreTheirLinuxPaths()
    {
        var context = new WslContext(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/bin/nono", "/home/u");

        var paths = WslTreeCopy.InDistro(SandboxHost.Windows(context),
            @"\\wsl.localhost\Ubuntu\home\u\FreshRSS", @"\\wsl.localhost\Ubuntu\home\u\.cache\visual-relay\wt\x");

        Assert.Equal((context, "/home/u/FreshRSS", "/home/u/.cache/visual-relay/wt/x"), paths);
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\Debian\home\u\repo", @"\\wsl.localhost\Ubuntu\home\u\wt")]
    [InlineData(@"C:\repo", @"\\wsl.localhost\Ubuntu\home\u\wt")]
    public void InDistro_APathOutsideTheResolvedDistro_KeepsTheAppSideCopy(string source, string dest)
    {
        var context = new WslContext(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/bin/nono", "/home/u");

        Assert.Null(WslTreeCopy.InDistro(SandboxHost.Windows(context), source, dest));
        Assert.Null(WslTreeCopy.InDistro(SandboxHost.Local, "/home/u/repo", "/home/u/wt"));
    }

    private static void SkipOnWindows() =>
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the scripts run inside the distro; /bin/sh stands in for it");

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
            : ["-c", script, "vr-overlay", Source, Dest, limitKb, .. names];
        var (_, output, _) = await ProcessCapture.RunAsync("/bin/sh", args, _root, TimeSpan.FromSeconds(60), CancellationToken.None);
        return WslTreeCopy.FailedNames(output);
    }
}
