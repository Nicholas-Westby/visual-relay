namespace VisualRelay.Cli;

/// <summary>
/// Resolves the watchdog timeout for <c>test</c> and <c>check</c> from the
/// environment, preserving the launcher's seams: VISUAL_RELAY_TEST_TIMEOUT
/// (default 90s) for <c>test</c>, and VISUAL_RELAY_CHECK_TEST_TIMEOUT (default
/// 300s) for the <c>check</c> gate's test step. The parsing rule is exposed as a
/// pure function so it is testable without mutating process-global env state.
/// </summary>
public static class WatchdogTimeouts
{
    /// <summary>
    /// <paramref name="serial"/> selects the default timeout: 1800s in serial
    /// mode (one collection at a time), 90s otherwise. The
    /// <c>VISUAL_RELAY_TEST_TIMEOUT</c> env var wins when set.
    /// <para>
    /// The 90s is a deliberate SPEED BUDGET, not merely a hang guard: the suite is
    /// meant to finish inside it, and a run that does not is meant to say so. Do not
    /// raise it to make a slow suite pass — make the suite faster, or raise it for one
    /// run with the env var.
    /// </para>
    /// <para>
    /// Measured across 73 full-suite runs on a Windows box in one day, dotnet's own
    /// Total time in seconds:
    /// </para>
    /// <para>
    ///                n     min    p50    p90    max
    ///   build       12    47.0   53.6   63.9   69.3
    ///   no-build    61    45.9   52.2   62.1   84.3
    /// </para>
    /// <para>
    /// So the suite is NOT near the line: p90 is 62-64s against 90, and 72 of those 73
    /// runs never came close. The same suite finishes in 30-38s on an Apple Silicon VM,
    /// a 2.3x margin against that arm's 1.4x, so exposure differs far more than the
    /// suite does. The 73rd run exceeded 90s and was killed — more than 30% beyond the
    /// day's maximum, out of a quiet stretch, with no trend or clustering before it.
    /// That is an unexplained excursion rather than a sizing problem, and raising the
    /// ceiling would treat a symptom whose magnitude nobody has accounted for.
    /// </para>
    /// <para>
    /// The operational cost of a kill is worth knowing: a timed-out run prints NO
    /// totals, so it reports as an absence rather than as a slow run. Redirect and
    /// check the exit code (124) rather than reading it through a filter — see
    /// TROUBLESHOOTING.md.
    /// </para>
    /// </summary>
    public static TimeSpan ForTest(bool serial) =>
        Resolve(Environment.GetEnvironmentVariable("VISUAL_RELAY_TEST_TIMEOUT"), serial ? 1800 : 90);

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
