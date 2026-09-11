using System.Runtime.CompilerServices;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// <see cref="ProcessCapture"/> with an injected <see cref="IProcessTreeControl"/>:
/// the stop sequence and the CPU sampling loop go through the strategy instead of
/// the host's signals and <c>ps</c>. Driven through the internal seams the capture
/// itself calls, under a <see cref="ManualTimeProvider"/>: no process, no real
/// waiting, and every await is on a signal the production code completes.
/// </summary>
public sealed class ProcessCaptureTreeControlTests
{
    private static readonly TimeSpan PastTheGraceWindow = TimeSpan.FromSeconds(11);

    [Fact]
    public async Task StopTree_GracefulFirst_ThenForcedAfterTheGraceWindow_ThenTheLocalKill()
    {
        var tp = new ManualTimeProvider();
        var control = new RecordingTreeControl();
        var localKills = 0;

        var stop = ProcessCapture.StopTreeAsync(control, hasExited: () => false, killLocal: () => localKills++, tp);

        // The polite request went out at once; nothing forced while the grace window runs.
        Assert.Equal([true], control.Stops);
        Assert.Equal(0, localKills);
        Assert.False(stop.IsCompleted);

        tp.Advance(PastTheGraceWindow);
        await stop;

        Assert.Equal([true, false], control.Stops);
        Assert.Equal(1, localKills);
    }

    [Fact]
    public async Task StopTree_TreeExitsDuringTheGraceWindow_SkipsTheForcedStop()
    {
        var tp = new ManualTimeProvider();
        var control = new RecordingTreeControl();
        var exited = new StrongBox<bool>(false);
        var localKills = 0;

        var stop = ProcessCapture.StopTreeAsync(control, () => exited.Value, () => localKills++, tp);
        exited.Value = true; // the tree honoured the polite stop
        tp.Advance(TimeSpan.FromMilliseconds(200));
        await stop;

        Assert.Equal([true], control.Stops);
        Assert.Equal(0, localKills);
    }

    [Fact]
    public async Task StopTree_ControlFailing_StillKillsTheLocalProcess()
    {
        var tp = new ManualTimeProvider();
        var control = new RecordingTreeControl { StopFailure = new InvalidOperationException("wsl.exe is gone") };
        var localKills = 0;

        var stop = ProcessCapture.StopTreeAsync(control, () => false, () => localKills++, tp);
        tp.Advance(PastTheGraceWindow);
        await stop;

        Assert.Equal([true, false], control.Stops);
        Assert.Equal(1, localKills);
    }

    [Fact]
    public async Task SampleLoop_AsksTheControlOnTheInterval_AndPulsesCpuOnGrowth()
    {
        var tp = new ManualTimeProvider();
        var control = new RecordingTreeControl(1_000);
        var pulsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var loop = ProcessCapture.SampleTreeCpuLoopAsync(
            control.SampleCpuMsAsync, intervalMs: 1_000,
            onActivity: signal => { if (signal == "cpu") pulsed.TrySetResult(); },
            onWedgeSample: null, socketProbe: null, tp, cts.Token);

        Assert.Equal(0, control.Samples);
        tp.Advance(TimeSpan.FromSeconds(1));
        await pulsed.Task;

        Assert.Equal(1, control.Samples);
        await cts.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task SampleLoop_NoGrowth_ReportsAnIdleSubtreeAndNoPulse()
    {
        var tp = new ManualTimeProvider();
        var control = new RecordingTreeControl(0);
        var pulses = new List<string>();
        var wedge = new TaskCompletionSource<ActivityWatchdog.WedgeSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var loop = ProcessCapture.SampleTreeCpuLoopAsync(
            control.SampleCpuMsAsync, intervalMs: 1_000,
            onActivity: pulses.Add,
            onWedgeSample: sample => wedge.TrySetResult(sample), socketProbe: () => true, tp, cts.Token);

        tp.Advance(TimeSpan.FromSeconds(1));
        var reported = await wedge.Task;

        Assert.True(reported.SubtreeIdle);
        Assert.True(reported.BackendSocketEstablished);
        Assert.Empty(pulses);
        await cts.CancelAsync();
        await loop;
    }

    /// <summary>
    /// The wiring proof: a real child under <c>ProcessCapture.RunAsync</c> with a
    /// strategy has its stop and its CPU sampling routed through the strategy
    /// and is still killed locally at the end. Opt-in because it spawns a shell; the
    /// sequence itself is asserted above without one. Still virtual-clock: the kill
    /// token is cancelled up front, so the stop sequence starts synchronously right
    /// after the spawn and one clock advance drives both it and the sampler.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithATreeControl_RoutesStopAndSamplingThroughIt()
    {
        NonoIntegration.SkipIfNotOptedIn();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the child is a POSIX shell");

        var tp = new ManualTimeProvider();
        var control = new RecordingTreeControl(1_000);
        var pulsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var kill = new CancellationTokenSource();
        await kill.CancelAsync();

        var run = ProcessCapture.RunAsync(
            "/bin/sh", ["-c", "tail -f /dev/null"], Path.GetTempPath(), TimeSpan.FromMinutes(5), CancellationToken.None,
            killToken: kill.Token, onActivity: signal => { if (signal == "cpu") pulsed.TrySetResult(); },
            cpuSampleIntervalMs: 1_000, timeProvider: tp, treeControl: control);

        // Before RunAsync's first real await the polite stop went out and both the
        // sampler's timer and the stop's first poll are registered on the clock.
        Assert.Equal([true], control.Stops);
        tp.Advance(PastTheGraceWindow);
        await pulsed.Task;
        var (exitCode, _, timedOut) = await run;

        Assert.Equal([true, false], control.Stops);
        Assert.Equal(1, control.Samples);
        Assert.False(timedOut);
        Assert.NotEqual(0, exitCode);
    }

    private sealed class RecordingTreeControl(params long?[] samples) : IProcessTreeControl
    {
        private readonly Queue<long?> _samples = new(samples);

        public List<bool> Stops { get; } = [];
        public int Samples { get; private set; }
        public Exception? StopFailure { get; init; }

        public Task<long?> SampleCpuMsAsync(CancellationToken ct)
        {
            Samples++;
            return Task.FromResult(_samples.Count > 0 ? _samples.Dequeue() : null);
        }

        public Task StopAsync(bool graceful, CancellationToken ct)
        {
            Stops.Add(graceful);
            return StopFailure is null ? Task.CompletedTask : Task.FromException(StopFailure);
        }
    }
}
