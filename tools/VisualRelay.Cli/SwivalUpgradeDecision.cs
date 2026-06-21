namespace VisualRelay.Cli;

/// <summary>
/// Pure decisions for the weekly swival upgrade check (the timing/parse logic
/// extracted from the launcher's <c>_swival_upgrade_check</c>). The surrounding
/// command owns the IO (reading the XDG stamp, running the probe); these helpers
/// own only the rules so they are testable without a clock or filesystem.
/// </summary>
public static class SwivalUpgradeDecision
{
    /// <summary>
    /// True when the probe should run: the stamp is absent/unparseable, or the
    /// last check is at least <paramref name="intervalSecs"/> old.
    /// </summary>
    public static bool ShouldProbe(long? lastCheckedEpochSecs, long nowEpochSecs, long intervalSecs)
    {
        if (lastCheckedEpochSecs is null)
            return true;
        return nowEpochSecs - lastCheckedEpochSecs.Value >= intervalSecs;
    }

    /// <summary>
    /// True when the probe reported an available upgrade — i.e. it emitted any
    /// non-whitespace output (mirrors the launcher's "non-empty ⇒ upgrade").
    /// </summary>
    public static bool UpgradeAvailable(string? probeOutput) =>
        !string.IsNullOrWhiteSpace(probeOutput);
}
