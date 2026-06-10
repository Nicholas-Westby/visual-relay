using System.Diagnostics;
using System.Text;

namespace VisualRelay.Core.Execution;

internal static class ProcessCapture
{
    public static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null,
        IReadOnlySet<string>? envRemove = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments);
        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken, environment, killToken, onActivity, envRemove);
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
        IReadOnlySet<string>? envRemove = null)
    {
        var startInfo = new ProcessStartInfo(fileName);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken, environment, killToken, onActivity, envRemove);
    }

    private static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        ProcessStartInfo startInfo,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null,
        IReadOnlySet<string>? envRemove = null)
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
}
