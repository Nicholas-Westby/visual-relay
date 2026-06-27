using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner
{
    // Resolve the first-output / inactivity watchdog windows for a tier: a tier
    // present in the per-tier map uses ITS configured window; an absent tier (or a
    // null inactivity map) falls back to the flat default. Extracted from RunAsync
    // (and split into its own partial to keep RunAsync.cs under the file-size guard)
    // so the runner→tier→window wiring is unit-testable without a real clock.
    internal static (int FirstOutputMs, int InactivityMs) ResolveTierWindows(RelayConfig config, string tier)
    {
        var firstOutputMs = config.FirstOutputTimeoutMsByTier.TryGetValue(tier, out var ctMs)
            ? ctMs : config.FirstOutputTimeoutMs;
        var inactivityMs = config.InactivityTimeoutMsByTier?.TryGetValue(tier, out var itMs) == true
            ? itMs : config.InactivityTimeoutMs;
        return (firstOutputMs, inactivityMs);
    }
}
