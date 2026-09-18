namespace VisualRelay.Cli;

/// <summary>
/// Resolves the watchdog timeout for <c>test</c> and <c>check</c> from the
/// environment, preserving the launcher's seams: VISUAL_RELAY_TEST_TIMEOUT
/// (default 300s) for <c>test</c>, and VISUAL_RELAY_CHECK_TEST_TIMEOUT (default
/// 300s) for the <c>check</c> gate's test step. The parsing rule is exposed as a
/// pure function so it is testable without mutating process-global env state.
/// </summary>
public static class WatchdogTimeouts
{
    /// <summary>
    /// <paramref name="serial"/> selects the default timeout: 1800s in serial
    /// mode (one collection at a time), 300s otherwise. The
    /// <c>VISUAL_RELAY_TEST_TIMEOUT</c> env var wins when set.
    /// <para>
    /// The parallel default was 60s, which is BELOW the runtime of the thing it
    /// guards. Measured across five consecutive passes on one Windows box, all of
    /// them healthy: 58.8s, 55.8s, 53.0s, 66.1s, 53.3s. A watchdog under 60s kills
    /// a normal pass roughly whenever the machine is busy, and it does so silently —
    /// the run prints no totals, so what a reader sees is an absence rather than a
    /// failure. Two of five earlier passes on the same box died that way.
    /// </para>
    /// <para>
    /// 300s is about 4.5x the slowest healthy pass observed, which leaves room for a
    /// loaded machine while still ending a genuine hang in minutes rather than never.
    /// A hang is better found with <c>--blame-hang</c>, which names the stuck test;
    /// this is the backstop behind it, and a backstop that fires on healthy runs is
    /// worse than a slow one.
    /// </para>
    /// </summary>
    public static TimeSpan ForTest(bool serial) =>
        Resolve(Environment.GetEnvironmentVariable("VISUAL_RELAY_TEST_TIMEOUT"), serial ? 1800 : 300);

    public static TimeSpan ForCheck() =>
        Resolve(Environment.GetEnvironmentVariable("VISUAL_RELAY_CHECK_TEST_TIMEOUT"), 300);

    /// <summary>
    /// Returns <paramref name="rawValue"/> seconds when it is a positive integer,
    /// otherwise <paramref name="defaultSecs"/>.
    /// </summary>
    public static TimeSpan Resolve(string? rawValue, int defaultSecs)
    {
        if (!string.IsNullOrEmpty(rawValue) && int.TryParse(rawValue, out var secs) && secs > 0)
            return TimeSpan.FromSeconds(secs);
        return TimeSpan.FromSeconds(defaultSecs);
    }
}
