using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using static VisualRelay.Tests.CommandToolTestHarness;

namespace VisualRelay.Tests;

/// <summary>
/// Every command tool reaches a process through the one hardened sandbox path, with
/// no opt-out: the shared <c>BuildNonoPrefix</c> builder, <c>rollback: false</c> on
/// the agent path (the git-based undo Visual Relay already keeps covers what nono's
/// rollback did, at a fraction of the 730 ms it cost per spawn), the network left
/// reachable, and one POSIX process group per command so it stays independently
/// killable.
/// </summary>
public sealed class CommandToolSandboxTests
{
    /// <summary>run_command is launched as nono wrapping the model's program, never as the program itself.</summary>
    [Fact]
    public async Task RunCommand_WrapsTheProgramInTheNonoSandbox()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        await tool.InvokeAsync(
            Arguments("""{"command":["dotnet","build","-warnaserror"]}"""), Context(600), CancellationToken.None);

        Assert.Equal("nono", launcher.FileName);
        Assert.Equal(
            new[] { "run", "--profile", NonoProfileEnsurer.ResolveProfilePath(), "--allow-cwd", "-a", TaskTemplates.ResolveUserTemplatesDir(), "--silent", "--" },
            launcher.Arguments.Take(8));
        Assert.Equal(new[] { "dotnet", "build", "-warnaserror" }, launcher.LaunchedCommand);
    }

    /// <summary>The agent path drops nono's rollback: the flags never appear.</summary>
    [Fact]
    public async Task RunCommand_DropsNonoRollbackOnTheAgentPath()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        await tool.InvokeAsync(Arguments("""{"command":["true"]}"""), Context(600), CancellationToken.None);

        Assert.DoesNotContain("--rollback", launcher.Arguments);
        Assert.DoesNotContain("--no-rollback-prompt", launcher.Arguments);
    }

    /// <summary>The sandbox never blocks the network: the relay must reach the model backend.</summary>
    [Fact]
    public async Task CommandTools_NeverBlockTheNetwork()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        var launcher = new RecordingCommandLauncher();
        IAgentTool argvTool = new RunCommandTool(Executor(launcher));
        IAgentTool shellTool = new RunShellCommandTool(Executor(launcher));

        await argvTool.InvokeAsync(Arguments("""{"command":["true"]}"""), Context(600), CancellationToken.None);
        Assert.DoesNotContain("--block-net", launcher.Arguments);

        await shellTool.InvokeAsync(Arguments("""{"command":"true"}"""), Context(600), CancellationToken.None);
        Assert.DoesNotContain("--block-net", launcher.Arguments);
    }

    /// <summary>run_shell_command reaches a NON-login shell, with the flag and the command line as separate argv entries.</summary>
    [Fact]
    public async Task RunShellCommand_UsesANonLoginShellWithSeparateArgvEntries()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix /bin/sh launch (Windows uses cmd.exe)");
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunShellCommandTool(Executor(launcher));

        await tool.InvokeAsync(
            Arguments("""{"command":"ls -la | head -3"}"""), Context(600), CancellationToken.None);

        Assert.Equal("nono", launcher.FileName);
        Assert.Equal(new[] { "/bin/sh", "-c", "ls -la | head -3" }, launcher.LaunchedCommand);
    }

    /// <summary>Commands run at the repository root, which is exactly what --allow-cwd grants writes to.</summary>
    [Fact]
    public async Task CommandTools_RunAtTheRepositoryRoot()
    {
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        await tool.InvokeAsync(Arguments("""{"command":["true"]}"""), Context(600), CancellationToken.None);

        Assert.Equal(TargetRoot, launcher.WorkingDirectory);
    }

    /// <summary>snapshot is a command tool like any other: its git status/diffstat runs through the same sandbox.</summary>
    [Fact]
    public async Task Snapshot_RunsItsGitReportThroughTheSameSandbox()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix /bin/sh launch (Windows uses cmd.exe)");
        var launcher = new RecordingCommandLauncher(output: "## main\n M src/x.cs");
        IAgentTool tool = new SnapshotTool(Executor(launcher));

        var result = await tool.InvokeAsync(Arguments("{}"), Context(600), CancellationToken.None);

        Assert.Equal("nono", launcher.FileName);
        Assert.Equal(new[] { "/bin/sh", "-c", SnapshotTool.SnapshotCommand }, launcher.LaunchedCommand);
        Assert.Contains("## main", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A non-zero exit is a result the model must read, not a tool error that feeds the consecutive-error guardrail.</summary>
    [Fact]
    public async Task NonZeroExit_IsReportedWithoutFlaggingAToolError()
    {
        var launcher = new RecordingCommandLauncher(exitCode: 1, output: "2 tests failed");
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["dotnet","test"]}"""), Context(600), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("exit code 1", result.Content, StringComparison.Ordinal);
        Assert.Contains("2 tests failed", result.Content, StringComparison.Ordinal);
    }

    /// <summary>The real launcher spawns through ProcessCapture, which is what puts each command in its own POSIX process group.</summary>
    [Fact]
    public void RealLauncher_SpawnsThroughProcessCaptureSoEachCommandOwnsItsProcessGroup()
    {
        var launchSource = File.ReadAllText(Path.Combine(
            RepoSetup.Root, "src", "VisualRelay.Core", "Agent", "Tools", "SandboxedCommandExecutor.Launch.cs"));
        Assert.Contains("ProcessCapture.RunAsync(", launchSource, StringComparison.Ordinal);

        var captureSource = File.ReadAllText(Path.Combine(
            RepoSetup.Root, "src", "VisualRelay.Core", "Execution", "ProcessCapture.cs"));
        Assert.Contains("SetProcessGroup(stageGroupId.Value, stageGroupId.Value)", captureSource, StringComparison.Ordinal);
        Assert.Contains("KillProcessGroup(stageGroupId.Value)", captureSource, StringComparison.Ordinal);
    }

    /// <summary>The command tools advertise the wire names, descriptions and schemas the model is given.</summary>
    [Fact]
    public void CommandTools_AdvertiseTheirWireNamesAndSchemas()
    {
        var launcher = new RecordingCommandLauncher();
        var executor = Executor(launcher);

        var argv = new RunCommandTool(executor).Definition;
        Assert.Equal("run_command", argv.Name);
        Assert.Contains("no shell", argv.Description, StringComparison.Ordinal);
        Assert.Equal("array", argv.ParametersSchema["properties"]!["command"]!["type"]!.GetValue<string>());

        var shell = new RunShellCommandTool(executor).Definition;
        Assert.Equal("run_shell_command", shell.Name);
        Assert.Equal("string", shell.ParametersSchema["properties"]!["command"]!["type"]!.GetValue<string>());

        var snapshot = new SnapshotTool(executor).Definition;
        Assert.Equal("snapshot", snapshot.Name);
        Assert.Contains("timeout_seconds", snapshot.ParametersSchema["properties"]!.ToJsonString(), StringComparison.Ordinal);
    }
}
