using System.Text.Json;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// The one path every command tool runs through: resolve the timeout, apply the
/// in-process command guard, wrap the command in the hardened sandbox, spawn it in
/// its own process group, and describe the outcome to the model.
/// <para>The sandbox is always on and there is no opt-out. The prefix comes from the
/// shared <c>SandboxedStage.BuildNonoPrefix</c> builder — the same one the
/// stage launch and <c>SandboxedTestRunner</c> use — with
/// <c>rollback: false</c>. Dropping nono's rollback is what the spec asks for and
/// what the measurements support (bare spawn 3.6 ms, nono without rollback 127 ms,
/// nono with rollback 730 ms); the undo Visual Relay already keeps — <c>run-base.txt</c>,
/// <c>pre-run-untracked.txt</c>, <c>WorktreeResetter</c>, <c>RewriteUndoStore</c> —
/// covers what rollback did, per commit, for the whole run.</para>
/// </summary>
/// <param name="config">Supplies the sandbox allow-list and the target command environment.</param>
/// <param name="launcher">Spawns the wrapped command. Defaults to the real process launcher.</param>
/// <param name="verboseDiagnostics">Output-only: shows nono's own banner instead of <c>--silent</c>.</param>
/// <param name="timeProvider">Clock for the elapsed-time report. Null uses system time.</param>
public sealed partial class SandboxedCommandExecutor(
    RelayConfig config,
    SandboxedCommandLauncher? launcher = null,
    bool verboseDiagnostics = false,
    TimeProvider? timeProvider = null)
{
    private readonly SandboxedCommandLauncher _launcher = launcher ?? RealLauncher;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Runs an argv-form command: the program and its arguments reach the sandbox
    /// verbatim, with no shell parsing.
    /// </summary>
    /// <param name="toolName">The calling tool, recorded in the guard payload.</param>
    /// <param name="argv">The program followed by its arguments.</param>
    /// <param name="arguments">The model's raw arguments, read for <c>timeout_seconds</c>.</param>
    /// <param name="context">The run this call belongs to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What the model sees.</returns>
    public Task<ToolResult> RunArgvAsync(
        string toolName, IReadOnlyList<string> argv, JsonElement arguments,
        ToolContext context, CancellationToken cancellationToken) =>
        RunAsync(toolName, argv, shell: null, arguments, context, cancellationToken);

    /// <summary>Runs a shell-form command through the same sandbox and guard.</summary>
    /// <param name="toolName">The calling tool, recorded in the guard payload.</param>
    /// <param name="command">The shell command line.</param>
    /// <param name="arguments">The model's raw arguments, read for <c>timeout_seconds</c>.</param>
    /// <param name="context">The run this call belongs to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What the model sees.</returns>
    public Task<ToolResult> RunShellAsync(
        string toolName, string command, JsonElement arguments,
        ToolContext context, CancellationToken cancellationToken) =>
        RunAsync(toolName, argv: null, command, arguments, context, cancellationToken);

    private async Task<ToolResult> RunAsync(
        string toolName, IReadOnlyList<string>? argv, string? shell, JsonElement arguments,
        ToolContext context, CancellationToken cancellationToken)
    {
        var (budget, timeoutError) = CommandTimeoutBudget.Resolve(arguments, context.RemainingStageBudget);
        if (budget is null)
            return ToolResult.Error(timeoutError!);

        // The guard runs in-process, before anything is spawned, for every command
        // tool — there is no middleware file to be missing and no tool that skips it.
        var verdict = argv is not null
            ? AgentCommandGuard.Inspect(toolName, argv)
            : AgentCommandGuard.Inspect(toolName, shell!);

        if (Refuse(verdict) is { } refusal)
            return refusal;

        var (fileName, launchArguments, launchError) = BuildLaunch(context.TargetRoot, verdict);
        if (launchError is not null)
            return ToolResult.Error(launchError);

        var environment = SandboxedStage.BuildTargetCommandEnvironment(config);
        var startedAt = _timeProvider.GetTimestamp();

        CommandRunOutcome outcome;
        try
        {
            outcome = await _launcher(
                fileName, launchArguments, context.TargetRoot, budget.Applied,
                environment.Overrides, environment.Remove, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolResult.Error(
                $"the sandboxed command could not be launched ({fileName}): {exception.Message}");
        }

        return Describe(outcome, budget, verdict.Rewritten, _timeProvider.GetElapsedTime(startedAt));
    }

    /// <summary>
    /// The refusal a denied command produces, or null when the guard allowed it.
    /// Nothing is spawned on a denial: the commit-authority gate is not advisory.
    /// </summary>
    /// <param name="verdict">What the in-process guard decided.</param>
    /// <returns>The refusal, or null.</returns>
    internal static ToolResult? Refuse(AgentCommandGuard.Verdict verdict) =>
        verdict.DenyReason is null
            ? null
            : ToolResult.Error($"blocked by the command guard: {verdict.DenyReason}");
}
