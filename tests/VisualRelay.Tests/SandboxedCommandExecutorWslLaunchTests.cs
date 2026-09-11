using System.Text.Json;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The Windows arm of <see cref="SandboxedCommandExecutor"/>: the agent's command
/// tools launch wsl.exe running the envelope, which starts nono inside the distro
/// on the Linux workspace behind the UNC root; a shell command is <c>/bin/sh -c</c>
/// inside the distro. A workspace the policy refuses, or a Windows box without a
/// usable distro, is refused before anything is launched.
/// </summary>
public sealed class SandboxedCommandExecutorWslLaunchTests
{
    private const string WslExe = @"C:\Windows\System32\wsl.exe";
    private const string Root = @"\\wsl$\Ubuntu\home\alice\repo";

    private static readonly WslContext Context = new(WslExe, "Ubuntu", "/usr/local/bin/nono", "/home/alice");
    private static readonly ToolContext WslRoot = new(Root, TimeSpan.FromSeconds(600));
    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task RunShell_UncRoot_LaunchesWslRunningTheShellUnderNonoInsideTheDistro()
    {
        var launcher = new RecordingCommandLauncher();
        var executor = Executor(launcher, SandboxHost.Windows(Context));

        var result = await executor.RunShellAsync(
            "run_shell_command", "ls -la | head -3", NoArguments, WslRoot, CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.Equal(WslExe, launcher.FileName);
        Assert.Equal(new[] { "-d", "Ubuntu", "--exec", "/bin/sh", "-c", WslLauncher.Envelope, "vr" }, launcher.Arguments.Take(7));
        Assert.EndsWith("-tool.pid", launcher.Arguments[7]);
        Assert.Equal("/home/alice/repo", launcher.Arguments[8]);
        Assert.Equal("env", launcher.Arguments[9]);
        Assert.Contains("/usr/local/bin/nono", launcher.Arguments);
        Assert.Equal(new[] { "/bin/sh", "-c", "ls -la | head -3" }, launcher.LaunchedCommand);
        Assert.DoesNotContain("cmd.exe", launcher.Arguments);
        // wsl.exe is started from the Windows temp directory: its own working
        // directory only has to be one it can translate, and the envelope cds to
        // the workspace inside the distro itself.
        Assert.Equal(Path.GetTempPath(), launcher.WorkingDirectory);
    }

    [Fact]
    public async Task RunArgv_UncRoot_HandsTheProgramToNonoInsideTheDistroWithTheWslEnvironmentAndAControl()
    {
        var launcher = new RecordingCommandLauncher();
        var executor = Executor(launcher, SandboxHost.Windows(Context));

        await executor.RunArgvAsync(
            "run_command", ["dotnet", "build", "-warnaserror"], NoArguments, WslRoot, CancellationToken.None);

        Assert.Equal(new[] { "dotnet", "build", "-warnaserror" }, launcher.LaunchedCommand);
        Assert.Equal(Path.GetTempPath(), launcher.WorkingDirectory);
        Assert.Equal(WslExeEnvironment.Variables, launcher.Environment);
        Assert.IsType<WslProcessTreeControl>(launcher.TreeControl);
    }

    [Fact]
    public async Task RunArgv_DriveRoot_IsRefusedByTheDrvFsPolicyBeforeAnythingIsLaunched()
    {
        var launcher = new RecordingCommandLauncher();
        var executor = Executor(launcher, SandboxHost.Windows(Context));

        var result = await executor.RunArgvAsync(
            "run_command", ["true"], NoArguments, new ToolContext(@"C:\repo", TimeSpan.FromSeconds(600)), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("DrvFs", result.Content);
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public async Task RunArgv_WindowsWithoutAUsableDistro_IsBlockedBeforeAnythingIsLaunched()
    {
        var launcher = new RecordingCommandLauncher();
        var executor = Executor(launcher, SandboxHost.Windows(null));

        var result = await executor.RunArgvAsync("run_command", ["true"], NoArguments, WslRoot, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(WslSandboxLauncher.BlockedMessage, result.Content);
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public async Task RunArgv_LocalHost_LaunchesNonoDirectlyWithoutAControl()
    {
        var launcher = new RecordingCommandLauncher();
        var executor = Executor(launcher, SandboxHost.Local);

        await executor.RunArgvAsync(
            "run_command", ["true"], NoArguments, new ToolContext(Path.GetTempPath(), TimeSpan.FromSeconds(600)), CancellationToken.None);

        Assert.Equal("nono", launcher.FileName);
        Assert.Null(launcher.TreeControl);
    }

    private static SandboxedCommandExecutor Executor(RecordingCommandLauncher launcher, SandboxHost host) =>
        new(TestConfig(), launcher.Launcher, host: host);

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int> { ["cheap"] = 90_000 },
            FirstOutputTimeoutMs: 660_000);
}
