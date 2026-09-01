using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

public sealed partial class FirstPartySubagentRunner
{
    /// <summary>How often the stall clocks are checked.</summary>
    private static readonly TimeSpan WatchdogTick = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The stall windows configured for a tier, falling back to the flat value.
    /// </summary>
    /// <param name="config">The repository's relay configuration.</param>
    /// <param name="tier">The tier the stage is running at.</param>
    /// <returns>The first-output, inactivity and output-silence windows.</returns>
    /// <remarks>
    /// These six config fields drove the subprocess runner's watchdog and had no
    /// consumer once it was deleted. They describe stalls, not subprocesses, so
    /// they drive the in-process watchdog now rather than being dropped from the
    /// config and silently ignored on every repo that set them.
    /// </remarks>
    internal static (TimeSpan FirstOutput, TimeSpan Inactivity, TimeSpan OutputSilence)
        ResolveStallWindows(RelayConfig config, string tier)
    {
        var firstOutput = config.FirstOutputTimeoutMsByTier.TryGetValue(tier, out var fo)
            ? fo : config.FirstOutputTimeoutMs;
        var inactivity = config.InactivityTimeoutMsByTier?.TryGetValue(tier, out var ia) == true
            ? ia : config.InactivityTimeoutMs;
        var outputSilence = config.OutputSilenceTimeoutMsByTier?.TryGetValue(tier, out var os) == true
            ? os : config.OutputSilenceTimeoutMs;

        return (
            TimeSpan.FromMilliseconds(firstOutput),
            TimeSpan.FromMilliseconds(inactivity),
            TimeSpan.FromMilliseconds(outputSilence));
    }

    /// <summary>
    /// Runs <paramref name="body"/> under a stall watchdog, cancelling it when a
    /// clock fires.
    /// </summary>
    /// <param name="watchdog">The armed watchdog.</param>
    /// <param name="body">The loop to supervise.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The loop's result, or a stalled result when the watchdog fired.</returns>
    /// <remarks>
    /// A stall is the absence of events, so it cannot be noticed by an event
    /// sink alone — something has to look at the clock while nothing happens.
    /// That is what this timer is for.
    /// </remarks>
    private async Task<AgentLoopResult> SuperviseAsync(
        AgentWatchdog watchdog,
        Func<CancellationToken, Task<AgentLoopResult>> body,
        CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var fired = AgentWatchdogOutcome.Disarmed;

        watchdog.Start();
        using var timer = _timeProvider.CreateTimer(
            _ =>
            {
                var (outcome, _) = watchdog.Evaluate();
                if (outcome == AgentWatchdogOutcome.Disarmed) return;
                fired = outcome;
                try { stall.Cancel(); }
                catch (ObjectDisposedException) { /* the loop already finished */ }
            },
            null, WatchdogTick, WatchdogTick);

        try
        {
            return await body(stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (fired != AgentWatchdogOutcome.Disarmed)
        {
            return new AgentLoopResult(
                AgentLoopOutcome.Error,
                string.Empty,
                new AgentStats(),
                $"the stage stalled: {Describe(fired)}");
        }
    }

    private static string Describe(AgentWatchdogOutcome outcome) => outcome switch
    {
        AgentWatchdogOutcome.FiredStall => "nothing happened for the inactivity window",
        AgentWatchdogOutcome.FiredSocketWedge =>
            "a request was in flight but produced no output while work continued",
        AgentWatchdogOutcome.FiredOutputSilence => "the model produced nothing for the silence window",
        AgentWatchdogOutcome.FiredAbsoluteCeiling => "the stage hit its absolute ceiling",
        _ => outcome.ToString(),
    };
}
