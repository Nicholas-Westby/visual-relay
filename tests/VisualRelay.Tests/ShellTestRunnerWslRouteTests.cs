using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The bootstrap's validation shell (<see cref="ShellTestRunner"/>, unsandboxed)
/// follows the workspace: a repository opened through the distro's UNC share is
/// validated by <c>/bin/sh -c</c> inside that distro, through the same envelope
/// the sandboxed launches use (so the workspace binding is loud and a timeout can
/// stop the Linux tree), with no nono in front. cmd.exe remains only where no
/// usable distro exists.
/// </summary>
public sealed class ShellTestRunnerWslRouteTests
{
    private const string WslExe = @"C:\Windows\System32\wsl.exe";
    private const string Root = @"\\wsl.localhost\Ubuntu\home\alice\repo";

    private static readonly WslContext Context = new(WslExe, "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    [Fact]
    public void ResolveLaunch_UncRootWithAContext_RunsTheShellInsideTheDistroWithoutNono()
    {
        var launch = ShellTestRunner.ResolveLaunch("dotnet test", Root, loginShell: false, SandboxHost.Windows(Context));

        Assert.Equal(WslExe, launch.FileName);
        Assert.Equal(new[] { "-d", "Ubuntu", "--exec", "/bin/sh", "-c", WslLauncher.Envelope, "vr" }, launch.Arguments.Take(7));
        Assert.EndsWith("-bootstrap.pid", launch.Arguments[7]);
        Assert.Equal(new[] { "/home/alice/repo", "env", "/bin/sh", "-c", "dotnet test" }, launch.Arguments.Skip(8));
        Assert.DoesNotContain("nono", launch.Arguments);
        Assert.Equal(WslExeEnvironment.Variables, launch.Environment);
        Assert.IsType<WslProcessTreeControl>(launch.TreeControl);
        // wsl.exe is started from the Windows temp directory, never the UNC
        // workspace: its own working directory only has to be one it can translate,
        // and the envelope cds to the workspace inside the distro anyway.
        Assert.Equal(Path.GetTempPath(), launch.StartIn(Root));
    }

    [Fact]
    public void ResolveLaunch_WindowsWithoutAContext_KeepsTheCmdBatch()
    {
        var launch = ShellTestRunner.ResolveLaunch("echo hi", @"C:\repo", loginShell: false, SandboxHost.Windows(null));

        Assert.Equal("cmd.exe", launch.FileName);
        Assert.Equal("/c", launch.Arguments[0]);
        Assert.EndsWith(".cmd", launch.Arguments[1]);
        Assert.Null(launch.TreeControl);
    }

    [Theory]
    [InlineData(true, "-lc")]
    [InlineData(false, "-c")]
    public void ResolveLaunch_LocalHost_IsBinSh(bool loginShell, string flag)
    {
        var launch = ShellTestRunner.ResolveLaunch("dotnet test", "/home/alice/repo", loginShell, SandboxHost.Local);

        Assert.Equal("/bin/sh", launch.FileName);
        Assert.Equal(new[] { flag, "dotnet test" }, launch.Arguments);
        Assert.Null(launch.TreeControl);
        // A local launch still follows the workspace.
        Assert.Equal("/home/alice/repo", launch.StartIn("/home/alice/repo"));
    }

    [Fact]
    public async Task RunAsync_DriveRootWithAContext_IsRefusedByThePolicyWithoutSpawning()
    {
        var runner = new ShellTestRunner(TimeSpan.FromSeconds(1), loginShell: false, host: SandboxHost.Windows(Context));

        var result = await runner.RunAsync(@"C:\Users\alice\repo", "dotnet test");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(WslWorkspacePolicy.Decide("/mnt/c/Users/alice/repo").Message, result.Output);
    }
}
