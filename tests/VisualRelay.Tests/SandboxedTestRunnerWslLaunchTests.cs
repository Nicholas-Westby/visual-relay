using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The Windows arm of <see cref="SandboxedTestRunner"/>: a workspace opened through
/// the distro's UNC share runs its verify command as wsl.exe executing the fixed
/// envelope, which starts nono inside the distro on the Linux workspace; a shell
/// verdict is <c>/bin/sh -c</c> INSIDE the distro, never a cmd.exe batch. The host
/// is injected, so the arm is pinned on any OS without a process.
/// </summary>
public sealed class SandboxedTestRunnerWslLaunchTests
{
    private const string WslExe = @"C:\Windows\System32\wsl.exe";
    // A distro no machine has. Building the launch reads <root>\.git, and a share path on
    // a distro that exists boots it when it is stopped: measured on Windows, 2.7 s idle and
    // 16 to 45 s per test under the suite's load. A missing distro answers in about 25 ms
    // and boots nothing.
    private const string Root = @"\\wsl.localhost\VrNoSuchDistro\home\alice\repo";
    private const string LinuxProfile = "/home/alice/.config/visual-relay/vr-guard.json";

    private static readonly WslContext Context = new(WslExe, "VrNoSuchDistro", "/usr/local/bin/nono", "/home/alice");
    private static readonly SandboxHost Host = SandboxHost.Windows(Context);

    [Fact]
    public void ResolveLaunch_UncRoot_RunsTheShellVerifyUnderNonoInsideTheDistro()
    {
        var sut = new SandboxedTestRunner(new ShellTestRunner(), TestConfig(), host: Host);

        var (fileName, args) = sut.ResolveLaunch("go test ./...", Root);

        Assert.Equal(WslExe, fileName);
        Assert.Equal(new[] { "-d", "VrNoSuchDistro", "--exec", "/bin/sh", "-c", WslLauncher.Envelope, "vr" }, args.Take(7));
        Assert.StartsWith(WslProcessControl.PidFileDirectory + "/", args[7]);
        Assert.EndsWith("-verify.pid", args[7]);
        Assert.Equal("/home/alice/repo", args[8]);
        // The target environment travels as env arguments: a wsl.exe child's Windows
        // environment does not cross into Linux.
        Assert.Equal(
            new[]
            {
                "env", "MSBUILDDISABLENODEREUSE=1", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
                "GRADLE_OPTS=-Dorg.gradle.daemon=false -Dorg.gradle.project.kotlin.compiler.execution.strategy=in-process",
            },
            args.Skip(9).Take(4));
        Assert.Equal(
            new[]
            {
                "/usr/local/bin/nono", "run", "--profile", LinuxProfile, "--allow-cwd", "-a", TemplatesGrant,
                "--diagnostics-json", "--silent", "--",
            },
            args.Skip(13).Take(10));
        Assert.Equal(new[] { "/bin/sh", "-c", "go test ./..." }, args.Skip(23));
    }

    [Fact]
    public void ResolveLaunch_DirectExecInner_RunsTheScriptUnderNonoInsideTheDistro()
    {
        var sut = new SandboxedTestRunner(new DirectExecTestRunner(), TestConfig(), host: Host);

        var (fileName, args) = sut.ResolveLaunch("./scripts/guard.sh --strict", Root);

        Assert.Equal(WslExe, fileName);
        Assert.Equal(new[] { "./scripts/guard.sh", "--strict" }, args.Skip(args.ToList().IndexOf("--") + 1));
        Assert.DoesNotContain("cmd.exe", args);
    }

    [Fact]
    public void ResolveSandboxedLaunch_UncRoot_CarriesTheWslEnvironmentAndAControlForTheLinuxTree()
    {
        var sut = new SandboxedTestRunner(new ShellTestRunner(), TestConfig(), host: Host);

        var launch = sut.ResolveSandboxedLaunch("go test ./...", Root);

        // wsl.exe itself gets only the two WSL variables; nothing is stripped from it.
        Assert.Equal(WslExeEnvironment.Variables, launch.Environment);
        Assert.Empty(launch.EnvironmentRemove);
        Assert.IsType<WslProcessTreeControl>(launch.TreeControl);
        // And it is started from the Windows temp directory, not the UNC workspace.
        Assert.Equal(Path.GetTempPath(), launch.StartIn(Root));
    }

    [Fact]
    public void ResolveLaunch_DriveRoot_IsRefusedWithTheDrvFsPolicyMessage()
    {
        var sut = new SandboxedTestRunner(new ShellTestRunner(), TestConfig(), host: Host);

        var ex = Assert.Throws<InvalidOperationException>(() => sut.ResolveLaunch("go test ./...", @"C:\Users\alice\repo"));

        Assert.Equal(WslWorkspacePolicy.Decide("/mnt/c/Users/alice/repo").Message, ex.Message);
    }

    [Fact]
    public void ResolveLaunch_UncRootInAnotherDistro_IsRefused()
    {
        var sut = new SandboxedTestRunner(new ShellTestRunner(), TestConfig(), host: Host);

        var ex = Assert.Throws<InvalidOperationException>(
            () => sut.ResolveLaunch("go test ./...", @"\\wsl.localhost\Debian\home\alice\repo"));

        Assert.Contains("'Debian'", ex.Message);
        Assert.Contains("'VrNoSuchDistro'", ex.Message);
        Assert.Contains("VR_WSL_DISTRO", ex.Message);
    }

    [Fact]
    public void ResolveLaunch_WindowsWithoutAUsableDistro_IsBlockedNeverUnsandboxed()
    {
        var sut = new SandboxedTestRunner(new ShellTestRunner(), TestConfig(), host: SandboxHost.Windows(null));

        var ex = Assert.Throws<InvalidOperationException>(() => sut.ResolveLaunch("go test ./...", Root));

        Assert.Equal(WslSandboxLauncher.BlockedMessage, ex.Message);
    }

    [Fact]
    public void ResolveSandboxedLaunch_LocalHost_IsTheNonoLaunchWithTheTargetEnvironmentAndNoControl()
    {
        var sut = new SandboxedTestRunner(new ShellTestRunner(), TestConfig(), host: SandboxHost.Local);

        var launch = sut.ResolveSandboxedLaunch("go test ./...", "/home/alice/repo");

        Assert.Equal("nono", launch.FileName);
        Assert.Equal("1", launch.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Null(launch.TreeControl);
    }

    private static string TemplatesGrant => Host.MapGrant(TaskTemplates.ResolveUserTemplatesDir())!;

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int> { ["cheap"] = 90_000 },
            FirstOutputTimeoutMs: 660_000);
}
