using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class ActivityWatchdogDecisionTests
{
    // ── (D) Output-silence ceiling loop tests ──────────────────────────────

    /// <summary>
    /// Hung-LLM incident shape through the virtualized loop: a real-output burst
    /// (stdout/trace), then the "cpu" pulse keeps firing every step (masking the
    /// ordinary inactivity deadline) while real output goes completely silent.
    /// The output-silence gate must fire at the per-tier limit, well before any
    /// absolute ceiling would, with Outcome.FiredOutputSilence.
    /// </summary>
    [Fact]
    public async Task WaitAsync_CpuPulseMasksDeadline_OutputSilenceFires()
    {
        const int outputSilenceMs = 1_500;
        var time = new ManualTimeProvider();
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: 600_000,
            absoluteCeilingMs: 0, kill, timeProvider: time,
            outputSilenceTimeoutMs: outputSilenceMs);

        // Early real output (stdout/trace), then only cpu pulses — no real output.
        watchdog.Pulse("trace");
        var watchdogTask = watchdog.WaitAsync(CancellationToken.None);
        const int step = outputSilenceMs / 4;
        for (var i = 0; i < 60 && !watchdogTask.IsCompleted; i++)
        {
            watchdog.Pulse("cpu");
            time.Advance(TimeSpan.FromMilliseconds(step));
        }
        for (var i = 0; i < 5 && !watchdogTask.IsCompleted; i++)
            await Task.Yield();
        var result = await watchdogTask;

        Assert.Equal(ActivityWatchdog.Outcome.FiredOutputSilence, result.Outcome);
        Assert.True(kill.IsCancellationRequested);
        Assert.True(result.SilenceMs >= outputSilenceMs,
            $"output-silence SilenceMs should be ≥ {outputSilenceMs}ms, got {result.SilenceMs}ms");
    }

    /// <summary>
    /// Healthy case: continuous real-output pulses (stdout/trace) keep the
    /// real-output silence clock low, far below the output-silence limit.
    /// The watchdog stays Disarmed across many windows — no false kill.
    /// </summary>
    [Fact]
    public async Task WaitAsync_ContinuousRealOutput_OutputSilenceGateNotFired()
    {
        const int outputSilenceMs = 2_000;
        var time = new ManualTimeProvider();
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: 600_000,
            absoluteCeilingMs: 0, kill, timeProvider: time,
            outputSilenceTimeoutMs: outputSilenceMs);

        watchdog.Pulse("trace");
        watchdog.Pulse("cpu");

        using var stopCts = new CancellationTokenSource();
        var watchdogTask = watchdog.WaitAsync(stopCts.Token);
        const int step = outputSilenceMs / 4;
        for (var i = 0; i < 16 && !watchdogTask.IsCompleted; i++)
        {
            watchdog.Pulse("trace"); // real output — resets real-output clock
            time.Advance(TimeSpan.FromMilliseconds(step));
        }
        await stopCts.CancelAsync();
        for (var i = 0; i < 5 && !watchdogTask.IsCompleted; i++)
            await Task.Yield();
        var result = await watchdogTask;

        Assert.Equal(ActivityWatchdog.Outcome.Disarmed, result.Outcome);
        Assert.False(kill.IsCancellationRequested);
    }
}
