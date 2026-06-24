using System.Diagnostics;
using System.Text.RegularExpressions;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class SandboxedTestRunner
{
    // CPU-tree sampling cadence for the filesystem-independent liveness pulse
    // (mirrors SwivalSubagentRunner). It is what distinguishes a busy-but-silent
    // test run (CPU pulses keep the run alive — never reaped) from a finished or
    // stalled one (no pulses → the idle deadline fires). Must stay well below
    // TestIdleGraceMilliseconds so a busy window is sampled several times before
    // the grace elapses.
    private const int CpuPulseSampleIntervalMs = 4_000;

    [GeneratedRegex(@"exited with code\s+(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex InnerExitCodeRegex();

    /// <summary>
    /// Parses the inner command's real exit status from the sandbox wrapper's
    /// captured output (nono prints "Command exited with code N" when its
    /// supervised command finishes). Returns the LAST match's code — nono's
    /// final verdict, emitted after all of the inner command's own output — or
    /// null when no such marker is present (the inner command never reported
    /// completion: a genuine stall/hang, not a finished run).
    /// </summary>
    internal static int? TryParseInnerExitCode(string output)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        var matches = InnerExitCodeRegex().Matches(output);
        if (matches.Count == 0)
            return null;
        return int.TryParse(matches[^1].Groups[1].ValueSpan, out var code) ? code : null;
    }

    /// <summary>
    /// Maps a watched sandbox-wrapper run to a <see cref="TestRunResult"/>,
    /// surfacing the inner command's real red/green outcome rather than the
    /// wrapper's kill signal. The four cases (in order):
    /// (1) hard wall-clock cap fired → the tree was CPU-busy and never finished
    ///     (a genuine hang) → halt/timeout;
    /// (2) the completion marker is present → the inner command FINISHED → use
    ///     its real exit code, whether the wrapper then exited cleanly OR was
    ///     reaped on idle while orphans lingered (the actual fix);
    /// (3) reaped on idle with NO marker → the inner command never finished (a
    ///     stall, or silent from the start) → halt/timeout, never a fabricated
    ///     red/green;
    /// (4) the wrapper exited on its own without a marker → trust its exit code.
    /// </summary>
    internal static TestRunResult InterpretWatched(
        int wrapperExitCode, string output, bool hardCapTimedOut, bool reapedOnIdle,
        double hardCapMs, TimeSpan elapsed)
    {
        if (hardCapTimedOut)
            return new TestRunResult(-1,
                $"test command timed out after {hardCapMs:F0}ms\n\n{output}", true, elapsed);

        var inner = TryParseInnerExitCode(output);
        if (inner is not null)
            return new TestRunResult(inner.Value, output, false, elapsed);

        if (reapedOnIdle)
            return new TestRunResult(-1,
                "test command timed out: the process tree went output-silent and " +
                "CPU-idle for the grace window without the inner command reporting " +
                $"completion\n\n{output}", true, elapsed);

        return new TestRunResult(wrapperExitCode, output, false, elapsed);
    }

    /// <summary>
    /// Runs a sandbox-wrapped test command under an <see cref="ActivityWatchdog"/>
    /// + CPU-tree sampling so the wrapper (nono) is reaped once its supervised
    /// process tree goes output-silent AND CPU-idle for <paramref name="idleGraceMs"/>
    /// — rather than waiting out <paramref name="hardCap"/> for descendants that
    /// outlive the finished tests. <paramref name="hardCap"/> stays as the
    /// wall-clock backstop for a genuinely busy (CPU-active) hang the idle
    /// detector intentionally never trips. The outcome is interpreted by
    /// <see cref="InterpretWatched"/>.
    /// </summary>
    internal static async Task<TestRunResult> RunWatchedAsync(
        string fileName, IReadOnlyList<string> args, string rootPath,
        IReadOnlyDictionary<string, string>? environment,
        int firstOutputTimeoutMs, int idleGraceMs, TimeSpan hardCap,
        int cpuSampleIntervalMs, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        using var watchdogCts = new CancellationTokenSource();
        using var watchdogLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, watchdogCts.Token);

        // absoluteCeiling = 0: ProcessCapture's hardCap is the wall-clock backstop
        // (the only guard against a busy-forever hang the CPU pulse keeps alive);
        // the watchdog itself only reaps on first-output / inactivity (idle).
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs, idleGraceMs, absoluteCeilingMs: 0, watchdogCts);

        var processTask = ProcessCapture.RunAsync(
            fileName, args, rootPath, hardCap, cancellationToken,
            environment: environment, killToken: watchdogCts.Token,
            onActivity: watchdog.Pulse, cpuSampleIntervalMs: cpuSampleIntervalMs);
        var watchdogTask = watchdog.WaitAsync(watchdogLinkedCts.Token);

        var reapedOnIdle = false;
        // WhenAny may return processTask when the watchdog kill triggers a
        // near-simultaneous exit (race); also check watchdogCts so a reap is
        // never missed and misreported as a clean exit.
        if (await Task.WhenAny(processTask, watchdogTask) == watchdogTask
            || watchdogCts.IsCancellationRequested)
        {
            var wd = await watchdogTask;
            if (wd.Outcome != ActivityWatchdog.Outcome.Disarmed)
                reapedOnIdle = true; // killToken already reaped the wrapper tree
        }

        var (exitCode, output, timedOut) = await processTask;
        watchdogCts.Cancel();
        try { await watchdogTask; } catch (OperationCanceledException) { /* disarmed */ }

        return InterpretWatched(exitCode, output, timedOut, reapedOnIdle, hardCap.TotalMilliseconds, sw.Elapsed);
    }
}
