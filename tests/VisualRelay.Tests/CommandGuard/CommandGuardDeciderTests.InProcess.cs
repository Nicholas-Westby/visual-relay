using System.Text.Json;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.CommandGuard;

namespace VisualRelay.Tests.CommandGuard;

public sealed partial class CommandGuardDeciderTests
{
    // ═══════════════════════════════════════════════════════════════════
    // In-process call path — AgentCommandGuard calls the decider directly,
    // with no subprocess, so the policy applies in every target repository
    // and to every command tool (closing the old python-tool bypass).
    // These extend the argv-mode and shell-mode coverage above to that path
    // and pin it to the same verdicts the exec'd binary produced.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>In-process argv mode strips --no-verify from a git commit.</summary>
    [Fact]
    public void InProcess_Argv_GitCommit_StripsNoVerify()
    {
        var verdict = AgentCommandGuard.Inspect("run_command", ["git", "commit", "--no-verify", "-m", "x"]);

        Assert.Null(verdict.DenyReason);
        Assert.True(verdict.Rewritten);
        Assert.Equal(new[] { "git", "commit", "-m", "x" }, verdict.Argv);
    }

    /// <summary>In-process argv mode strips the n out of a combined short flag inside a commit.</summary>
    [Fact]
    public void InProcess_Argv_GitCommit_StripsNFromCombinedShortFlag()
    {
        var verdict = AgentCommandGuard.Inspect("run_command", ["git", "commit", "-anm", "x"]);

        Assert.True(verdict.Rewritten);
        Assert.Equal(new[] { "git", "commit", "-am", "x" }, verdict.Argv);
    }

    /// <summary>In-process argv mode leaves -n alone outside a commit, and passes non-git through.</summary>
    [Fact]
    public void InProcess_Argv_NonCommit_PassesThrough()
    {
        var clean = AgentCommandGuard.Inspect("run_command", ["git", "clean", "-n"]);
        Assert.False(clean.Rewritten);
        Assert.Equal(new[] { "git", "clean", "-n" }, clean.Argv);

        var grep = AgentCommandGuard.Inspect("run_command", ["rg", "-n", "--no-verify"]);
        Assert.False(grep.Rewritten);
        Assert.Equal(new[] { "rg", "-n", "--no-verify" }, grep.Argv);
    }

    /// <summary>In-process shell mode strips --no-verify byte-exactly.</summary>
    [Fact]
    public void InProcess_Shell_GitCommit_StripsNoVerify()
    {
        var verdict = AgentCommandGuard.Inspect("run_shell_command", "git commit --no-verify -m x");

        Assert.True(verdict.Rewritten);
        Assert.Equal("git commit -m x", verdict.Shell);
    }

    /// <summary>In-process shell mode strips only inside the commit segment of a chain.</summary>
    [Fact]
    public void InProcess_Shell_Chain_StripsOnlyTheCommitSegment()
    {
        var verdict = AgentCommandGuard.Inspect(
            "run_shell_command", "git clean -n && git commit -n -m x");

        Assert.True(verdict.Rewritten);
        Assert.Equal("git clean -n && git commit -m x", verdict.Shell);
    }

    /// <summary>In-process shell mode passes a non-git command line through unchanged.</summary>
    [Fact]
    public void InProcess_Shell_NonGit_PassesThrough()
    {
        var verdict = AgentCommandGuard.Inspect("run_shell_command", "make test | tail -5");

        Assert.False(verdict.Rewritten);
        Assert.Equal("make test | tail -5", verdict.Shell);
    }

    /// <summary>The in-process path produces exactly the verdict the exec'd binary's decider produced for the same command.</summary>
    [Fact]
    public void InProcess_MatchesTheDeciderVerdictForTheSameCommand()
    {
        var argvPayload = JsonDocument.Parse(
            """{"phase":"before","tool":"run_command","mode":"argv","command":["git","commit","-n","-m","x"]}""").RootElement;
        var argvExpected = (string[])CommandGuardDecider.Decide(argvPayload).Command!;
        Assert.Equal(argvExpected, AgentCommandGuard.Inspect("run_command", ["git", "commit", "-n", "-m", "x"]).Argv);

        var shellPayload = JsonDocument.Parse(
            """{"phase":"before","tool":"run_shell_command","mode":"shell","command":"git commit -n -m x"}""").RootElement;
        var shellExpected = (string)CommandGuardDecider.Decide(shellPayload).Command!;
        Assert.Equal(shellExpected, AgentCommandGuard.Inspect("run_shell_command", "git commit -n -m x").Shell);
    }

    /// <summary>A deny verdict survives the in-process mapping intact, carrying no command to run — the fail-closed half of the policy.</summary>
    [Fact]
    public void InProcess_DenyVerdict_CarriesNoCommand()
    {
        var verdict = AgentCommandGuard.Interpret(
            CommandGuardResult.Deny("command-guard internal error for git commit"),
            ["git", "commit"], null);

        Assert.Equal("command-guard internal error for git commit", verdict.DenyReason);
        Assert.Null(verdict.Argv);
        Assert.Null(verdict.Shell);
        Assert.False(verdict.Rewritten);
    }
}
