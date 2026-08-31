using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.CommandGuard;
using static VisualRelay.Tests.CommandToolTestHarness;

namespace VisualRelay.Tests;

/// <summary>
/// The command guard now runs IN-PROCESS, on every command tool.
///
/// <para>It used to be an external binary swival exec'd through
/// <c>--command-middleware</c>, wired only when <c>.githooks/command-guard</c> exists
/// in the target repository — a file Visual Relay never provisions, so every repository
/// but this one ran unguarded, and swival's <c>python</c> tool skipped the middleware
/// outright (89 uninspected calls in this repo's history). Calling the decider directly
/// closes both holes. The policy itself is unchanged: <c>--no-verify</c> always
/// stripped, <c>-n</c> and a combined short flag's <c>n</c> stripped only inside a
/// <c>git commit</c>, fail-open for non-git and fail-CLOSED for a git commit.</para>
/// </summary>
public sealed class CommandToolGuardTests
{
    /// <summary>run_command has --no-verify stripped from a git commit before anything is spawned, and the model is told.</summary>
    [Fact]
    public async Task RunCommand_StripsNoVerifyFromAGitCommit()
    {
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["git","commit","--no-verify","-m","wip"]}"""),
            Context(600), CancellationToken.None);

        Assert.Equal(new[] { "git", "commit", "-m", "wip" }, launcher.LaunchedCommand);
        Assert.Contains("removed git hook-bypass flags", result.Content, StringComparison.Ordinal);
    }

    /// <summary>The short bypass flag is stripped inside a git commit and left alone everywhere else.</summary>
    [Fact]
    public async Task RunCommand_StripsTheShortFlagOnlyInsideAGitCommit()
    {
        var commitLauncher = new RecordingCommandLauncher();
        IAgentTool commitTool = new RunCommandTool(Executor(commitLauncher));
        await commitTool.InvokeAsync(
            Arguments("""{"command":["git","commit","-nm","wip"]}"""), Context(600), CancellationToken.None);
        Assert.Equal(new[] { "git", "commit", "-m", "wip" }, commitLauncher.LaunchedCommand);

        var cleanLauncher = new RecordingCommandLauncher();
        IAgentTool cleanTool = new RunCommandTool(Executor(cleanLauncher));
        var result = await cleanTool.InvokeAsync(
            Arguments("""{"command":["git","clean","-n"]}"""), Context(600), CancellationToken.None);

        Assert.Equal(new[] { "git", "clean", "-n" }, cleanLauncher.LaunchedCommand);
        Assert.DoesNotContain("removed git hook-bypass flags", result.Content, StringComparison.Ordinal);
    }

    /// <summary>run_shell_command is inspected segment by segment, so a bypass hidden mid-chain is still stripped.</summary>
    [Fact]
    public async Task RunShellCommand_StripsABypassHiddenMidChain()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix /bin/sh launch (Windows uses cmd.exe)");
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunShellCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":"git add -A && git commit --no-verify -m wip"}"""),
            Context(600), CancellationToken.None);

        Assert.Equal("git add -A && git commit -m wip", launcher.LaunchedCommand[2]);
        Assert.Contains("removed git hook-bypass flags", result.Content, StringComparison.Ordinal);
    }

    /// <summary>The interpreter that swival's python tool used to bypass entirely is inspected like anything else — a commit chained onto it is still stripped.</summary>
    [Fact]
    public async Task RunShellCommand_InspectsCommandsTheOldPythonToolBypassed()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix /bin/sh launch (Windows uses cmd.exe)");
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunShellCommandTool(Executor(launcher));

        await tool.InvokeAsync(
            Arguments("""{"command":"python3 -c 'print(1)' && git commit -n -m generated"}"""),
            Context(600), CancellationToken.None);

        Assert.Equal("python3 -c 'print(1)' && git commit -m generated", launcher.LaunchedCommand[2]);
    }

    /// <summary>A non-git command is passed through untouched — the guard fails open for everything that is not a commit.</summary>
    [Fact]
    public async Task NonGitCommand_PassesThroughUntouched()
    {
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["grep","-rn","--no-verify","src"]}"""), Context(600), CancellationToken.None);

        Assert.Equal(new[] { "grep", "-rn", "--no-verify", "src" }, launcher.LaunchedCommand);
        Assert.DoesNotContain("removed git hook-bypass flags", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A deny verdict refuses the call and nothing is spawned: the fail-closed commit-authority gate is not advisory.</summary>
    [Fact]
    public void DenyVerdict_RefusesTheCallInsteadOfLaunching()
    {
        var verdict = AgentCommandGuard.Interpret(
            CommandGuardResult.Deny("command-guard internal error for git commit"), null, null);

        var refusal = SandboxedCommandExecutor.Refuse(verdict);

        Assert.NotNull(refusal);
        Assert.True(refusal.IsError);
        Assert.Equal(
            "blocked by the command guard: command-guard internal error for git commit",
            refusal.Content);
        Assert.Null(SandboxedCommandExecutor.Refuse(
            AgentCommandGuard.Interpret(CommandGuardResult.Allow, ["git", "status"], null)));
    }
}
