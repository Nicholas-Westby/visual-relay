using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The launch site shared by the agent's command tools and the verify runner on
/// Windows: the workspace VR was given (the distro's UNC share) becomes the Linux
/// path the envelope changes into, the workspace policy is applied, and every
/// refusal is a message rather than a guess about where to run.
/// </summary>
public sealed class WslSandboxLauncherTests
{
    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    private static readonly string[] Prefix = ["/usr/local/bin/nono", "run", "--allow-cwd", "--"];

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\alice\repo", "/home/alice/repo")]
    [InlineData(@"\\wsl$\Ubuntu\home\alice\my repo\", "/home/alice/my repo")]
    [InlineData("//wsl.localhost/ubuntu/home/alice/répo", "/home/alice/répo")]
    public void ResolveWorkspace_UncRootInTheContextsDistro_IsItsLinuxPath(string root, string expected)
    {
        var (linuxWorkspace, error) = WslSandboxLauncher.ResolveWorkspace(Context, root);

        Assert.Null(error);
        Assert.Equal(expected, linuxWorkspace);
    }

    [Fact]
    public void ResolveWorkspace_UncRootInAnotherDistro_IsRefusedNamingBothDistrosAndTheSelector()
    {
        var (linuxWorkspace, error) = WslSandboxLauncher.ResolveWorkspace(Context, @"\\wsl.localhost\Debian\home\alice\repo");

        Assert.Null(linuxWorkspace);
        Assert.Contains("'Debian'", error);
        Assert.Contains("'Ubuntu'", error);
        Assert.Contains("VR_WSL_DISTRO=Debian", error);
    }

    [Fact]
    public void ResolveWorkspace_DriveRoot_GetsTheDrvFsPolicyDecision()
    {
        var (linuxWorkspace, error) = WslSandboxLauncher.ResolveWorkspace(Context, @"C:\Users\alice\repo");

        Assert.Null(linuxWorkspace);
        Assert.Equal(WslWorkspacePolicy.Decide("/mnt/c/Users/alice/repo").Message, error);
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("/home/alice/repo")]
    [InlineData(@"\\server\share\repo")]
    [InlineData("")]
    public void ResolveWorkspace_RootNotInsideADistro_IsRefused(string root)
    {
        var (linuxWorkspace, error) = WslSandboxLauncher.ResolveWorkspace(Context, root);

        Assert.Null(linuxWorkspace);
        Assert.Contains(@"\\wsl.localhost\<distro>\", error);
    }

    [Fact]
    public void Build_WrapsTheCommandInTheEnvelopeOnTheLinuxWorkspaceWithAFreshPidFile()
    {
        var (first, error) = WslSandboxLauncher.Build(
            Context, @"\\wsl.localhost\Ubuntu\home\alice\repo", Prefix, "/bin/sh", ["-c", "go test"],
            new Dictionary<string, string> { ["CI"] = "1" }, "verify");
        var (second, _) = WslSandboxLauncher.Build(
            Context, @"\\wsl.localhost\Ubuntu\home\alice\repo", Prefix, "true", [], null, "verify");

        Assert.Null(error);
        Assert.NotNull(first);
        Assert.Equal("/home/alice/repo", first.LinuxWorkspace);
        Assert.StartsWith(WslProcessControl.PidFileDirectory + "/", first.PidFile);
        Assert.EndsWith("-verify.pid", first.PidFile);
        Assert.NotEqual(first.PidFile, second!.PidFile);
        Assert.Equal(
            WslLauncher.Build(
                Context.WslExePath, "Ubuntu", "/home/alice/repo", first.PidFile, Prefix, "/bin/sh", ["-c", "go test"],
                new Dictionary<string, string> { ["CI"] = "1" }).Arguments,
            first.Launch.Arguments);
        Assert.Equal(Context.WslExePath, first.Launch.FileName);
    }

    [Fact]
    public void Build_CarriesTheUsersLoginPath_SoProfileInstalledToolchainsResolve()
    {
        var context = Context with { UserPath = "/home/alice/.cargo/bin:/usr/bin" };

        var (launch, _) = WslSandboxLauncher.Build(
            context, @"\\wsl.localhost\Ubuntu\home\alice\repo", Prefix, "/bin/sh", ["-c", "cargo test"],
            new Dictionary<string, string> { ["CI"] = "1" }, "verify");

        Assert.Contains("PATH=/home/alice/.cargo/bin:/usr/bin", launch!.Launch.Arguments);
        Assert.Contains("CI=1", launch.Launch.Arguments);
    }

    [Fact]
    public void Build_WithoutAUserPath_LeavesPathToWsl()
    {
        var (launch, _) = WslSandboxLauncher.Build(
            Context, @"\\wsl.localhost\Ubuntu\home\alice\repo", Prefix, "true", [], null, "tool");

        Assert.DoesNotContain(launch!.Launch.Arguments, a => a.StartsWith("PATH=", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RefusedWorkspace_YieldsTheRefusalAndNoLaunch()
    {
        var (launch, error) = WslSandboxLauncher.Build(Context, @"C:\repo", Prefix, "true", [], null, "tool");

        Assert.Null(launch);
        Assert.Contains("DrvFs", error);
    }

    [Fact]
    public void TreeControl_SamplesAndStopsTheTreeBehindThePidFile()
    {
        var control = WslSandboxLauncher.TreeControl(Context, "/tmp/visual-relay/x-tool.pid");

        Assert.IsType<WslProcessTreeControl>(control);
    }

    [Fact]
    public void BlockedMessage_NamesWslNonoTheGateAndTheConsequence()
    {
        Assert.Contains("WSL2", WslSandboxLauncher.BlockedMessage);
        Assert.Contains("nono", WslSandboxLauncher.BlockedMessage);
        Assert.Contains("visual-relay launch", WslSandboxLauncher.BlockedMessage);
        Assert.Contains("never runs a command unsandboxed", WslSandboxLauncher.BlockedMessage);
        Assert.Contains("inside", WslSandboxLauncher.BlockedMessage);
    }
}
