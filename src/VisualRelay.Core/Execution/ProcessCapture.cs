using System.Diagnostics;
using System.Text;

namespace VisualRelay.Core.Execution;

internal static class ProcessCapture
{
    // CPU delta per sample window that counts as real work rather than
    // scheduler dust from an idle-blocked process.
    private const long CpuPulseEpsilonMs = 50;

    public static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null,
        IReadOnlySet<string>? envRemove = null,
        int cpuSampleIntervalMs = 0)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments);
        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken, environment, killToken, onActivity, envRemove, cpuSampleIntervalMs);
    }

    public static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null,
        IReadOnlySet<string>? envRemove = null,
        int cpuSampleIntervalMs = 0)
    {
        var startInfo = new ProcessStartInfo(fileName);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken, environment, killToken, onActivity, envRemove, cpuSampleIntervalMs);
    }

    private static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        ProcessStartInfo startInfo,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null,
        IReadOnlySet<string>? envRemove = null,
        int cpuSampleIntervalMs = 0)
    {
        using var process = new Process();
        process.StartInfo = startInfo;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        if (envRemove is not null)
        {
            foreach (var key in envRemove)
            {
                process.StartInfo.EnvironmentVariables.Remove(key);
            }
        }
        if (environment is not null)
        {
            foreach (var kvp in environment)
            {
                process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }
        var output = new StringBuilder();
        // stdout and stderr fire on separate threads; StringBuilder is not thread-safe,
        // so all appends and reads of `output` must be serialized or it corrupts and
        // throws ("Destination is too short"), crashing the whole run.
        var outputLock = new object();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (outputLock) { output.AppendLine(e.Data); } onActivity?.Invoke("stdout"); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (outputLock) { output.AppendLine(e.Data); } onActivity?.Invoke("stderr"); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cpuCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cpuTask = cpuSampleIntervalMs > 0 && onActivity is not null
            ? SampleTreeCpuLoopAsync(process.Id, cpuSampleIntervalMs, onActivity, cpuCts.Token)
            : Task.CompletedTask;

        try
        {
            using var killRegistration = killToken.CanBeCanceled
                ? killToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch { /* already exited */ } })
                : default;

            var waitTask = process.WaitForExitAsync(cancellationToken);
            if (timeout != Timeout.InfiniteTimeSpan && await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken)) != waitTask)
            {
                process.Kill(entireProcessTree: true);
                lock (outputLock) { return (-1, output.ToString(), true); }
            }

            await waitTask;
            lock (outputLock) { return (process.ExitCode, output.ToString(), false); }
        }
        finally
        {
            cpuCts.Cancel();
            try { await cpuTask; } catch { /* sampler never propagates */ }
        }
    }

    /// <summary>
    /// Single decision point for whether a CPU sample merits a liveness pulse.
    /// Exposed as internal static so regression tests can exercise the exact
    /// production algorithm rather than an inlined copy.
    /// </summary>
    internal static (bool Pulse, long? NewBaseline) TryDecideCpuPulse(
        long? baseline, long sampleMs, long epsilonMs)
    {
        if (baseline is not null && sampleMs - baseline.Value >= epsilonMs)
            return (true, sampleMs);
        return (false, sampleMs);
    }

    /// <summary>
    /// Pulses onActivity("cpu") whenever the process tree accrues CPU between
    /// samples — the one activity signal the target repo's filesystem cannot
    /// freeze. Sampling failures (null return) invalidate the baseline so
    /// accumulated CPU during a failure gap can never cross the epsilon and
    /// emit a spurious pulse. The next successful sample silently
    /// re-establishes the baseline without signalling.
    /// </summary>
    private static async Task SampleTreeCpuLoopAsync(
        int rootPid, int intervalMs, Action<string> onActivity, CancellationToken ct)
    {
        // A process starts with zero accrued CPU, so 0 is a correct first
        // baseline — the first sample can already pulse. (A null-seeded
        // baseline would silently push the earliest pulse to 2× interval,
        // losing the race against small inactivity windows.)
        long? baseline = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(intervalMs, ct);
                var sample = ProcessTreeCpuSampler.TrySampleTreeCpuMs(rootPid);
                if (sample is null)
                {
                    baseline = null;
                    continue;
                }
                var (pulse, newBaseline) = TryDecideCpuPulse(baseline, sample.Value, CpuPulseEpsilonMs);
                if (pulse)
                    onActivity("cpu");
                baseline = newBaseline;
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch
        {
            // sampling must never break the capture
        }
    }
}
