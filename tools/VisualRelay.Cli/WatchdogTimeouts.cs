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
    /// <summary>
    /// <paramref name="serial"/> selects the default timeout: 1800s in serial
    /// mode (one collection at a time), 60s otherwise. The
    /// <c>VISUAL_RELAY_TEST_TIMEOUT</c> env var wins when set.
    /// <para>
    /// The 60s is a deliberate SPEED BUDGET, not merely a hang guard: the suite is
    /// meant to finish inside it, and a run that does not is meant to say so. Do not
    /// raise it to make a slow suite pass — make the suite faster, or raise it for one
    /// run with the env var.
    /// </para>
    /// <para>
    /// Known pressure on it, recorded so the next person has the numbers rather than
    /// rediscovering them: five consecutive healthy passes on a Windows box measured
    /// 58.8s, 55.8s, 53.0s, 66.1s and 53.3s, so that arm is already at or over budget
    /// and two earlier passes there were killed mid-run. macOS finishes the same suite
    /// in roughly half that. A killed run prints no totals, which is easy to misread as
    /// a pass when the exit code is taken through a pipe (see TROUBLESHOOTING.md).
    /// </para>
    /// </summary>
    public static TimeSpan ForTest(bool serial) =>
        Resolve(Environment.GetEnvironmentVariable("VISUAL_RELAY_TEST_TIMEOUT"), serial ? 1800 : 60);

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
