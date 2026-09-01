using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

public sealed partial class FirstPartySubagentRunner
{
    /// <summary>How often the stall clocks are checked.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

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
    private static (TimeSpan FirstOutput, TimeSpan Inactivity, TimeSpan OutputSilence)
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
    /// <returns>
    /// The loop's result, and the kill signature when a clock fired. The driver
    /// branches on that signature to decide flag-immediately versus
    /// escalate-and-retry, so discarding it makes a ceiling kill look like an
    /// ordinary failure a dearer tier could fix.
    /// </returns>
    /// <remarks>
    /// A stall is the absence of events, so it cannot be noticed by an event
    /// sink alone — something has to look at the clock while nothing happens.
    /// That is what this timer is for.
    /// </remarks>
    private async Task<(AgentLoopResult Result, KillSignature? Kill, bool HardAbort)> SuperviseAsync(
        AgentWatchdog watchdog,
        Func<CancellationToken, Task<AgentLoopResult>> body,
        CancellationToken cancellationToken)
    {
        var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var fired = AgentWatchdogOutcome.Disarmed;
        KillSignature? kill = null;

        watchdog.Start();
        var timer = _timeProvider.CreateTimer(
            _ =>
            {
                var (outcome, signature) = watchdog.Evaluate();
                if (outcome == AgentWatchdogOutcome.Disarmed) return;
                fired = outcome;
                kill = signature;
                // Safe: the finally awaits the timer's disposal, which waits for
                // this callback, BEFORE disposing the source.
                // ReSharper disable once AccessToDisposedClosure
                stall.Cancel();
            },
            null, Tick, Tick);

        try
        {
            return (await body(stall.Token).ConfigureAwait(false), null, false);
        }
        catch (OperationCanceledException) when (fired != AgentWatchdogOutcome.Disarmed)
        {
            return (
                new AgentLoopResult(
                    AgentLoopOutcome.Error,
                    string.Empty,
                    new AgentStats(),
                    $"the stage stalled: {Describe(fired)}"),
                kill,
                // A plain stall CAN be the model, so it stays escalatable. A
                // ceiling, an output-silence kill and a wedge are host
                // conditions a dearer tier would hit identically.
                AgentWatchdog.IsHardAbort(fired));
        }
        finally
        {
            // Await the timer's disposal before the source it cancels goes away.
            // Disposing the source first would let an in-flight tick call Cancel
            // on a disposed object.
            await timer.DisposeAsync().ConfigureAwait(false);
            stall.Dispose();
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
