using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VisualRelay.Core.Execution;

internal static partial class ProcessCapture
{
    // Bound the post-exit stdout/stderr drain so a fully detached pipe-holder
    // can never wedge the run to the timeout cap (see the reap-then-drain in RunAsync).
    private const int DrainGraceMs = 4000;

    private static readonly string[] LeakedAppleSdkEnvNames = ["DEVELOPER_DIR", "SDKROOT"];

    /// <summary>
    /// VR runs under <c>nix develop</c>, which exports <c>DEVELOPER_DIR</c>/<c>SDKROOT</c>
    /// pointing at the nix apple-sdk (for VR's own .NET build). A child process that
    /// invokes <c>/usr/bin/git</c> (the macOS xcrun shim) — an agent command inside nono,
    /// the verify test command — would treat that nix path as the developer directory, find no Command
    /// Line Tools there, and trigger the macOS "install the command line developer tools"
    /// dialog. Strip the LEAKED nix value so the child falls back to the xcode-select
    /// default; a real (non-nix) <c>DEVELOPER_DIR</c> set for the target is left intact.
    /// </summary>
    internal static void StripLeakedNixSdkEnv(StringDictionary env)
    {
        foreach (var name in LeakedAppleSdkEnvNames)
        {
            // ProcessStartInfo.EnvironmentVariables' indexer THROWS on a missing key
            // (unlike a plain StringDictionary, whose getter returns null), so guard
            // with ContainsKey first — otherwise a spawn with no DEVELOPER_DIR throws.
            if (!env.ContainsKey(name))
                continue;
            if (env[name] is { } current && current.Contains("/nix/store/", StringComparison.Ordinal))
                env.Remove(name);
        }
    }

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
        bool reapProcessTree = true,
        int cpuSampleIntervalMs = 0,
        Action<ActivityWatchdog.WedgeSample>? onWedgeSample = null,
        Func<bool>? socketProbe = null, TimeProvider? timeProvider = null,
        IProcessTreeControl? treeControl = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments);
        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken, environment, killToken, onActivity, envRemove, reapProcessTree, cpuSampleIntervalMs, onWedgeSample, socketProbe, timeProvider, treeControl);
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
        bool reapProcessTree = true,
        int cpuSampleIntervalMs = 0,
        Action<ActivityWatchdog.WedgeSample>? onWedgeSample = null,
        Func<bool>? socketProbe = null, TimeProvider? timeProvider = null,
        IProcessTreeControl? treeControl = null)
    {
        var startInfo = new ProcessStartInfo(fileName);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken, environment, killToken, onActivity, envRemove, reapProcessTree, cpuSampleIntervalMs, onWedgeSample, socketProbe, timeProvider, treeControl);
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
        bool reapProcessTree = true,
        int cpuSampleIntervalMs = 0,
        Action<ActivityWatchdog.WedgeSample>? onWedgeSample = null,
        Func<bool>? socketProbe = null, TimeProvider? timeProvider = null,
        IProcessTreeControl? treeControl = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        using var process = new Process();
        process.StartInfo = startInfo;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        // Always decode the child's streams as UTF-8. On Windows the default is the
        // console code page, which mangles the non-ASCII bytes a Linux tool (behind
        // wsl.exe) or any modern toolchain writes; on Unix UTF-8 is the default already.
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
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
        // Don't leak VR's own nix-build apple-sdk env into target subprocesses, or their
        // /usr/bin/git calls pop the macOS Command Line Tools install dialog (see method).
        StripLeakedNixSdkEnv(process.StartInfo.EnvironmentVariables);
        var output = new StringBuilder();
        // stdout and stderr fire on separate threads; StringBuilder is not thread-safe,
        // so all appends and reads of `output` must be serialized or it corrupts and
        // throws ("Destination is too short"), crashing the whole run.
        var outputLock = new object();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (outputLock) { output.AppendLine(e.Data); } onActivity?.Invoke("stdout"); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (outputLock) { output.AppendLine(e.Data); } onActivity?.Invoke("stderr"); } };

        // Signal the spawned process's OWN exit, independent of stream EOF: with stdout/
        // stderr read async, WaitForExitAsync waits for reader EOF, which a surviving
        // descendant holding the inherited pipe write-ends blocks. Race THIS instead.
        var exitedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exitedTcs.TrySetResult();
        process.Start();
        // Guard the race: the process may have exited before Exited was subscribed.
        if (process.HasExited)
            exitedTcs.TrySetResult();
        int? stageGroupId = null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            stageGroupId = process.Id;
            SetProcessGroup(stageGroupId.Value, stageGroupId.Value);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cpuCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The tree behind wsl.exe is invisible to the host: sample it (and later
        // stop it) through the injected strategy; otherwise use the host's own view.
        Func<CancellationToken, Task<long?>> sampler = treeControl is not null
            ? treeControl.SampleCpuMsAsync
            : HostTreeSampler(process.Id);
        var cpuTask = cpuSampleIntervalMs > 0 && onActivity is not null
            ? SampleTreeCpuLoopAsync(sampler, cpuSampleIntervalMs, onActivity, onWedgeSample, socketProbe, tp, cpuCts.Token)
            : Task.CompletedTask;

        try
        {
            // killRegistration DisposeAsync() completes only once an in-flight
            // callback has returned; GracefulStopThenKillAsync guards the disposed
            // process via SafeHasExited.
            await using var killRegistration = killToken.CanBeCanceled
                ? killToken.Register(() => { _ = GracefulStopThenKillAsync(process, stageGroupId, tp, treeControl); })
                : default;

            // Propagate cancellation, mirroring the old WaitForExitAsync(cancellationToken).
            await using var ctReg = cancellationToken.Register(() => exitedTcs.TrySetCanceled(cancellationToken));

            if (timeout != Timeout.InfiniteTimeSpan && await Task.WhenAny(exitedTcs.Task, Task.Delay(timeout, tp, cancellationToken)) != exitedTcs.Task)
            {
                await GracefulStopThenKillAsync(process, stageGroupId, tp, treeControl);
                lock (outputLock) { return (-1, output.ToString(), true); }
            }

            await exitedTcs.Task;
            if (reapProcessTree)
            {
                // Reap descendants so survivors release inherited pipe
                // write-ends; then bounded-drain → WaitForExitAsync EOFs fast.
                if (stageGroupId.HasValue)
                    try { KillProcessGroup(stageGroupId.Value); } catch { /* best-effort */ }
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            }
            await Task.WhenAny(process.WaitForExitAsync(CancellationToken.None), Task.Delay(TimeSpan.FromMilliseconds(DrainGraceMs), tp, CancellationToken.None));
            lock (outputLock) { return (process.ExitCode, output.ToString(), false); }
        }
        finally
        {
            cpuCts.Cancel();
            try { await cpuTask; } catch { /* sampler never propagates */ }
        }
    }

    // ── Process-group helpers (POSIX only) ─────────────────────────

    [DllImport("libc", SetLastError = true)]
    private static extern int setpgid(int pid, int pgid);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pgid, int sig);

    // ReSharper disable once InconsistentNaming — POSIX signal name from <signal.h>;
    // kept uppercase to match the C constant it mirrors.
    private const int SIGKILL = 9;

    private static void SetProcessGroup(int pid, int pgid)
    {
        // Best-effort: child may have already exec'd; ignore errno.
        _ = setpgid(pid, pgid);
    }

    private static void KillProcessGroup(int pgid)
    {
        // Safety: never kill group 0 (caller) or -1 (broadcast).
        // Also guard against accidentally targeting the host's own session.
        if (pgid <= 0 || pgid == Process.GetCurrentProcess().SessionId)
            return;
        _ = kill(-pgid, SIGKILL);
    }
}
