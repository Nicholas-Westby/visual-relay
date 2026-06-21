namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner
{
    /// <summary>
    /// User-facing reason for a stall that survived every retry. All variants lead
    /// with "persistent model-backend stall" — the phrase the GUI's stall-retry
    /// recovery and the watchdog regression tests key on — then append the
    /// phase-specific detail. The socket-wedge variant names the additive detector
    /// (no real output + idle agent subtree + ESTABLISHED backend socket) so the
    /// autopsy is unambiguous.
    /// </summary>
    private static string BuildPersistentStallReason(
        ActivityWatchdog.Result wdResult, int firstOutputMs, int inactivityMs, int maxStallAttempts)
    {
        if (wdResult.Outcome == ActivityWatchdog.Outcome.FiredSocketWedge)
        {
            return $"persistent model-backend stall: swival socket-wedged — no real output " +
                $"(stdout/stderr/trace) for {wdResult.SilenceMs}ms while the agent subtree sat " +
                $"idle (~0 CPU) with an ESTABLISHED connection to the model backend " +
                $"(inactivity window={inactivityMs}ms). " +
                $"{maxStallAttempts} attempts exhausted.";
        }

        var firstOutputPhase = wdResult.LastPulseSource == "none";
        return $"persistent model-backend stall: swival had no activity for " +
            $"{wdResult.SilenceMs}ms (phase={(firstOutputPhase ? "first-output" : "inactivity")}, " +
            $"threshold={(firstOutputPhase ? firstOutputMs : inactivityMs)}ms). " +
            $"Last signal: {wdResult.LastPulseSource}. " +
            $"{maxStallAttempts} attempts exhausted.";
    }
}
