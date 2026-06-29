using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Run-log event publishers for the RunAsync retry/escalation loop, split out of
// ProcessRunners.RunAsync.cs to keep that file under the size guard. All are
// best-effort and no-op when there is no event sink.
public sealed partial class SwivalSubagentRunner
{
    // A clearly-labeled escalation transition in the Run Log, e.g.
    //   "Stage 1 Ideate escalated (run 2/3): tier cheap→balanced, max-turns 200→400".
    // General-purpose: keyed only on the stage's number/name and generic tier/turn
    // values, never on VR-repo or test-framework symbols.
    private async Task PublishEscalationAsync(
        StageInvocation invocation, int attempt, int run, int maxRuns,
        string fromTier, string toTier, int fromTurns, int toTurns, CancellationToken cancellationToken)
    {
        if (_eventSink is null)
            return;
        var message = StageEscalation.DescribeTransition(
            invocation.Stage.Number, invocation.Stage.Name, run, maxRuns,
            fromTier, toTier, fromTurns, toTurns);
        await _eventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "stage_escalated",
            invocation.RunId, invocation.TargetRoot, invocation.TaskName,
            invocation.Stage.Number, invocation.Tier, attempt,
            Data: new Dictionary<string, string> { ["message"] = message }), cancellationToken);
    }

    private async Task PublishStallKillAsync(
        StageInvocation invocation, int attempt, ActivityWatchdog.Result wdResult,
        int firstOutputMs, int inactivityMs, int outputBytes, string? killedOutputPath,
        CancellationToken cancellationToken)
    {
        if (_eventSink is null)
            return;
        await _eventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "stall_kill",
            invocation.RunId, invocation.TargetRoot, invocation.TaskName,
            invocation.Stage.Number, invocation.Tier, attempt,
            Data: new Dictionary<string, string>
            {
                ["reason"] = wdResult.Outcome switch
                {
                    ActivityWatchdog.Outcome.FiredAbsoluteCeiling => "absolute_ceiling",
                    ActivityWatchdog.Outcome.FiredSocketWedge => "socket_wedge",
                    _ => "stall"
                },
                ["lastSignal"] = wdResult.LastPulseSource,
                ["silenceMs"] = wdResult.SilenceMs.ToString(),
                ["firstOutputTimeoutMs"] = firstOutputMs.ToString(),
                ["inactivityTimeoutMs"] = inactivityMs.ToString(),
                ["outputBytes"] = outputBytes.ToString(),
                ["outputSaved"] = killedOutputPath ?? "(persist failed)"
            }), cancellationToken);
    }

    private async Task PublishNonzeroExitAsync(
        StageInvocation invocation, int attempt, int exitCode, int outputBytes,
        string? killedOutputPath, CancellationToken cancellationToken)
    {
        if (_eventSink is null)
            return;
        await _eventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "nonzero_exit",
            invocation.RunId, invocation.TargetRoot, invocation.TaskName,
            invocation.Stage.Number, invocation.Tier, attempt,
            Data: new Dictionary<string, string>
            {
                ["exitCode"] = exitCode.ToString(),
                ["outputBytes"] = outputBytes.ToString(),
                ["outputSaved"] = killedOutputPath ?? "(persist failed)"
            }), cancellationToken);
    }
}
