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
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); onActivity?.Invoke("stdout"); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); onActivity?.Invoke("stderr"); } };
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
                return (-1, output.ToString(), true);
            }

            await waitTask;
            return (process.ExitCode, output.ToString(), false);
        }
        finally
        {
            cpuCts.Cancel();
            try { await cpuTask; } catch { /* sampler never propagates */ }
        }
    }

    /// <summary>
    /// Pulses onActivity("cpu") whenever the process tree accrues CPU between
    /// samples — the one activity signal the target repo's filesystem cannot
    /// freeze. Sampling failures are silent: no signal, never fake activity.
    /// </summary>
    private static async Task SampleTreeCpuLoopAsync(
        int rootPid, int intervalMs, Action<string> onActivity, CancellationToken ct)
    {
        // A process starts with zero accrued CPU, so 0 is a correct first
        // baseline — the first sample can already pulse. (A null-seeded
        // baseline would silently push the earliest pulse to 2× interval,
        // losing the race against small inactivity windows.)
        long baseline = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(intervalMs, ct);
                var sample = ProcessTreeCpuSampler.TrySampleTreeCpuMs(rootPid);
                if (sample is null)
                    continue;
                if (sample.Value - baseline >= CpuPulseEpsilonMs)
                    onActivity("cpu");
                baseline = sample.Value;
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
