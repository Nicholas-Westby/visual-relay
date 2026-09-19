using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Where a git command for a workspace root runs. A root inside a WSL distro
/// (a UNC path, or a Linux path once a distro is resolved) is served by the git
/// INSIDE that distro, so VR and the sandboxed agent share one filesystem view of
/// the repository; a Windows drive root keeps Git for Windows, so VR's own Windows
/// checkout and tooling are unchanged. Pure decisions and argv shapes, no process.
/// </summary>
public sealed class GitRoutingTests
{
    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "VrNoSuchDistro", "/usr/local/bin/nono", "/home/alice");

    [Theory]
    [InlineData(@"\\wsl$\VrNoSuchOtherDistro\home\u\my repo", "VrNoSuchOtherDistro", "/home/u/my repo")]
    [InlineData(@"\\wsl.localhost\VrNoSuchDistro\home\u\répo", "VrNoSuchDistro", "/home/u/répo")]
    [InlineData("//wsl.localhost/VrNoSuchDistro/home/u/repo/", "VrNoSuchDistro", "/home/u/repo")]
    public void Decide_UncRoot_RunsInTheDistroThePathNames(string root, string distro, string linuxRoot)
    {
        var expected = new GitRoute.Wsl(distro, linuxRoot);

        // The path names its distro, so no context is needed and none overrides it.
        Assert.Equal(expected, GitRouting.Decide(root, null));
        Assert.Equal(expected, GitRouting.Decide(root, Context));
    }

    [Fact]
    public void Decide_LinuxRoot_WithAContext_RunsInTheContextDistro() =>
        Assert.Equal(new GitRoute.Wsl("VrNoSuchDistro", "/home/alice/repo"), GitRouting.Decide("/home/alice/repo", Context));

    [Fact]
    public void Decide_LinuxRoot_WithoutAContext_IsNative() =>
        Assert.IsType<GitRoute.Native>(GitRouting.Decide("/Users/alice/repo", null));

    [Theory]
    [InlineData(@"C:\Dev\visual-relay")]
    [InlineData(@"D:\")]
    [InlineData(@"repo\sub")]
    [InlineData("relative/repo")]
    [InlineData("")]
    public void Decide_DriveOrRelativeRoot_IsNativeEvenWithAContext(string root) =>
        Assert.IsType<GitRoute.Native>(GitRouting.Decide(root, Context));

    [Fact]
    public void Launch_RunsGitAgainstTheLinuxRootThroughAPlainExec()
    {
        var route = new GitRoute.Wsl("VrNoSuchDistro", "/home/alice/my repo");

        var launch = GitRouting.Launch(route, Context.WslExePath, ["status", "--porcelain"], null);

        Assert.Equal(Context.WslExePath, launch.FileName);
        string[] expected = ["-d", "VrNoSuchDistro", "--exec", "git", "-C", "/home/alice/my repo", "status", "--porcelain"];
        Assert.Equal(expected, launch.Arguments);
        Assert.Equal(WslExeEnvironment.Variables, launch.Environment);
    }

    [Fact]
    public void Launch_CarriesTheCallerEnvironmentIntoTheDistro()
    {
        // A wsl.exe child's Windows environment never crosses into Linux, and the
        // sealed commit's token must reach the pre-commit hook inside the distro.
        var environment = new Dictionary<string, string>
        {
            ["RELAY_COMMIT_TOKEN"] = "abc 123",
            ["RELAY_NONCE"] = "abc 123",
        };

        var launch = GitRouting.Launch(new GitRoute.Wsl("VrNoSuchDistro", "/home/alice/repo"), "wsl.exe", ["commit", "-m", "x"], environment);

        string[] expected =
        [
            "-d", "VrNoSuchDistro", "--exec", "env", "RELAY_COMMIT_TOKEN=abc 123", "RELAY_NONCE=abc 123",
            "git", "-C", "/home/alice/repo", "commit", "-m", "x",
        ];
        Assert.Equal(expected, launch.Arguments);
    }

    [Fact]
    public void Launch_EmptyEnvironment_AddsNoEnvPrefix()
    {
        var launch = GitRouting.Launch(
            new GitRoute.Wsl("VrNoSuchDistro", "/home/alice/repo"), "wsl.exe", ["log"], new Dictionary<string, string>());

        Assert.Equal("git", launch.Arguments[3]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A=B")]
    public void Launch_RejectsAnEnvironmentNameThatWouldChangeTheShape(string name)
    {
        var environment = new Dictionary<string, string> { [name] = "v" };

        Assert.Throws<ArgumentException>(() =>
            GitRouting.Launch(new GitRoute.Wsl("VrNoSuchDistro", "/r"), "wsl.exe", ["status"], environment));
    }

    [Fact]
    public void WslExe_PrefersTheContextsExe_ElseTheNameWindowsResolvesOnPath()
    {
        Assert.Equal(Context.WslExePath, WslExe.Resolve(Context));
        Assert.Equal("wsl.exe", WslExe.Resolve(null));
    }
}
