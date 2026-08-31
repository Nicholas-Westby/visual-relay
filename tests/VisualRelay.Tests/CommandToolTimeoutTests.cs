using VisualRelay.Core.Agent.Tools;
using static VisualRelay.Tests.CommandToolTestHarness;

namespace VisualRelay.Tests;

/// <summary>
/// The headline behaviour of the command tools: there is no hidden per-call timeout
/// ceiling.
///
/// <para>Swival clamped every command to <c>MAX_TIMEOUT = 240</c> seconds with no flag
/// and no env var, and then reported <c>command timed out after 240s</c> — a number
/// the model never asked for. Unable to see where the limit came from, it retried at
/// the same doomed value; 378 of the 5,750 recorded command calls asked for more than
/// that cap. These tests pin the fix: a requested timeout is honoured verbatim, the
/// only ceiling is the remaining stage budget, any reduction is announced with the
/// requested value / the applied value / the reason, and a timeout names the number
/// that was ACTUALLY applied.</para>
/// </summary>
public sealed class CommandToolTimeoutTests
{
    /// <summary>A request above the remaining stage budget is reduced — and the whole result text says so, naming the requested value, the applied value and the reason.</summary>
    [Fact]
    public async Task RequestAboveTheStageBudget_IsReducedAndAnnounced()
    {
        var clock = new ManualTimeProvider();
        var launcher = new RecordingCommandLauncher(output: "built 3 projects", clock: clock, elapsedSeconds: 12);
        IAgentTool tool = new RunCommandTool(Executor(launcher, clock));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["make","build"],"timeout_seconds":900}"""), Context(420), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(420), launcher.Timeout);
        Assert.False(result.IsError);
        Assert.Equal(
            """
            Note: timeout_seconds=900 was reduced to 420s because only 420s remain of this stage's budget, which is the only ceiling on a command timeout. Re-running with a larger timeout_seconds cannot raise it.
            exit code 0 (ran for 12s of the 420s applied timeout)
            built 3 projects
            """,
            result.Content);
    }

    /// <summary>A request that fits inside the remaining stage budget is applied exactly as asked, with nothing announced.</summary>
    [Fact]
    public async Task RequestInsideTheStageBudget_IsHonouredVerbatim()
    {
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["true"],"timeout_seconds":900}"""), Context(1800), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(900), launcher.Timeout);
        Assert.DoesNotContain("reduced", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A timeout names the timeout that was actually applied, plus why it differs from the request — never a hidden constant.</summary>
    [Fact]
    public async Task Timeout_NamesTheAppliedValue_NotAHiddenCap()
    {
        var launcher = new RecordingCommandLauncher(exitCode: -1, output: "partial", timedOut: true);
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["true"],"timeout_seconds":900}"""), Context(420), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            """
            command timed out after 420s, the timeout that was actually applied. timeout_seconds=900 was reduced to 420s because only 420s remain of this stage's budget, which is the only ceiling on a command timeout. Re-running with a larger timeout_seconds cannot raise it.
            Output captured before the timeout:
            partial
            """,
            result.Content);
        Assert.DoesNotContain("240", result.Content, StringComparison.Ordinal);
    }

    /// <summary>When the request was honoured in full, the timeout text says so and tells the model a larger value would also be honoured.</summary>
    [Fact]
    public async Task Timeout_WithAnHonouredRequest_SaysNothingWasReduced()
    {
        var launcher = new RecordingCommandLauncher(exitCode: -1, timedOut: true);
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["true"],"timeout_seconds":300}"""), Context(1800), CancellationToken.None);

        Assert.Contains("command timed out after 300s", result.Content, StringComparison.Ordinal);
        Assert.Contains("exactly the timeout_seconds=300 you asked for", result.Content, StringComparison.Ordinal);
        Assert.Contains("1800s remained of this stage's budget", result.Content, StringComparison.Ordinal);
        Assert.Contains("No output was captured before the timeout.", result.Content, StringComparison.Ordinal);
    }

    /// <summary>With no timeout_seconds the documented default applies, and the timeout text names it and how to raise it.</summary>
    [Fact]
    public async Task NoRequest_AppliesTheNamedDefaultAndSaysHowToRaiseIt()
    {
        var launcher = new RecordingCommandLauncher(exitCode: -1, timedOut: true);
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["true"]}"""), Context(1800), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(120), launcher.Timeout);
        Assert.Contains("command timed out after 120s", result.Content, StringComparison.Ordinal);
        Assert.Contains("No timeout_seconds was given, so the 120s default applied",
            result.Content, StringComparison.Ordinal);
        Assert.Contains("up to 1800s", result.Content, StringComparison.Ordinal);
    }

    /// <summary>The default is itself bounded by the stage budget, and that reduction is announced too.</summary>
    [Fact]
    public async Task DefaultAboveTheStageBudget_IsReducedAndAnnounced()
    {
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["true"]}"""), Context(45), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(45), launcher.Timeout);
        Assert.Contains("no timeout_seconds was given, so the 120s default applied, and it was reduced to 45s",
            result.Content, StringComparison.Ordinal);
    }

    /// <summary>A non-positive timeout is rejected with a message that names the argument.</summary>
    [Fact]
    public async Task NonPositiveTimeout_IsRejectedWithoutLaunching()
    {
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["true"],"timeout_seconds":-5}"""), Context(1800), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("timeout_seconds must be a positive number of seconds", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, launcher.Calls);
    }

    /// <summary>An exhausted stage budget refuses the call outright rather than launching a command that cannot finish.</summary>
    [Fact]
    public async Task ExhaustedStageBudget_RefusesToLaunch()
    {
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":["true"],"timeout_seconds":10}"""), Context(0), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("the stage's time budget is exhausted", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, launcher.Calls);
    }

    /// <summary>run_shell_command shares the one timeout policy — the announcement is not special to the argv tool.</summary>
    [Fact]
    public async Task ShellTool_SharesTheSameTimeoutPolicy()
    {
        var launcher = new RecordingCommandLauncher();
        IAgentTool tool = new RunShellCommandTool(Executor(launcher));

        var result = await tool.InvokeAsync(
            Arguments("""{"command":"make build","timeout_seconds":1200}"""), Context(600), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(600), launcher.Timeout);
        Assert.Contains("timeout_seconds=1200 was reduced to 600s", result.Content, StringComparison.Ordinal);
    }
}
