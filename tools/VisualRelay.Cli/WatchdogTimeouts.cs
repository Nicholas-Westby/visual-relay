namespace VisualRelay.Cli;

/// <summary>
/// Resolves the watchdog timeout for <c>test</c> and <c>check</c> from the
/// environment, preserving the launcher's seams: VISUAL_RELAY_TEST_TIMEOUT
/// (default 60s) for <c>test</c>, and VISUAL_RELAY_CHECK_TEST_TIMEOUT (default
/// 300s) for the <c>check</c> gate's test step. The parsing rule is exposed as a
/// pure function so it is testable without mutating process-global env state.
/// </summary>
public static class WatchdogTimeouts
{
    public static TimeSpan ForTest() =>
        Resolve(Environment.GetEnvironmentVariable("VISUAL_RELAY_TEST_TIMEOUT"), 60);

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
