using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Guards that <see cref="ProcessCapture"/> honours the
/// <c>reapProcessTree</c> opt-out on the normal-exit path.
/// </summary>
public sealed class ProcessCaptureReapOptOutTests
{
    /// <summary>
    /// With <c>reapProcessTree:false</c> the normal-exit path still completes: a
    /// trivial child exits 0, does not hit the timeout, and its captured output is
    /// exactly what the child wrote (nothing).
    /// </summary>
    [Fact]
    public async Task ReapFalse_RunsTrivialCommand()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "runs /usr/bin/true in /tmp, which Windows lacks");
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            "/usr/bin/true",
            "",
            "/tmp",
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            reapProcessTree: false);

        Assert.False(timedOut, "Trivial command with reapProcessTree:false should not time out.");
        Assert.Equal(0, exitCode);
        // /usr/bin/true writes to neither stream, so the capture must come back empty.
        Assert.Equal("", output);
    }

    /// <summary>
    /// With nothing reaping it, a background grandchild keeps the output pipe open after
    /// the command exits. The capture stops waiting after its drain grace, as it must, and
    /// now says so in the output rather than handing back what it had as if it were all
    /// there was. The same cut happens when the pool is too busy to finish reading in
    /// time: a parallel run on macOS returned exit 0 with empty output twice on 2026-09-18.
    /// </summary>
    [Fact]
    public async Task ReapFalse_OutputHeldOpenPastTheDrainGrace_IsMarkedIncomplete()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a POSIX shell backgrounds the pipe holder");
        var tp = new ManualTimeProvider();
        var drainStarted = tp.TimerScheduledAsync(TimeSpan.FromMilliseconds(4000));
        // The holder blocks forever and nothing here reaps it, so the shell records its pid
        // before exiting and the test kills it.
        var pidFile = Path.Combine(Path.GetTempPath(), $"vr-drain-holder-{Guid.NewGuid():N}.pid");
        try
        {
            var run = ProcessCapture.RunAsync(
                "/bin/sh", $"-c \"echo started; tail -f /dev/null & echo $! > '{pidFile}'; exit 0\"", "/tmp",
                TimeSpan.FromHours(1), CancellationToken.None, reapProcessTree: false, timeProvider: tp);
            await drainStarted.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            tp.Advance(TimeSpan.FromSeconds(5));
            var (exitCode, output, timedOut) = await run;

            Assert.False(timedOut, "the command exited; only the reading was cut short");
            Assert.Equal(0, exitCode);
            Assert.Contains("may be incomplete", output, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            {
                try
                {
                    using var holder = System.Diagnostics.Process.GetProcessById(pid);
                    holder.Kill();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // Already gone.
                }
            }

            File.Delete(pidFile);
        }
    }

    /// <summary>
    /// The default (reaping) path behaves identically for a trivial child — the
    /// control case that proves the opt-out above changes nothing observable.
    /// </summary>
    [Fact]
    public async Task DefaultReap_RunsTrivialCommand()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "runs /usr/bin/true in /tmp, which Windows lacks");
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            "/usr/bin/true",
            "",
            "/tmp",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(timedOut, "Trivial command with default reap should not time out.");
        Assert.Equal(0, exitCode);
        // The reaping path must not inject anything of its own into the capture.
        Assert.Equal("", output);
    }
}
