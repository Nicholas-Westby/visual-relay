using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VisualRelay.Core.Execution;

internal static class ProcessCapture
{
    // CPU delta per sample window that counts as real work rather than
    // scheduler dust from an idle-blocked process.
    private const long CpuPulseEpsilonMs = 50;

    // Bound the post-exit stdout/stderr drain so a fully detached pipe-holder
    // can never wedge the run to the timeout cap (see the reap-then-drain in RunAsync).
    private const int DrainGraceMs = 4000;

    private static readonly string[] LeakedAppleSdkEnvNames = ["DEVELOPER_DIR", "SDKROOT"];

    /// <summary>
    /// VR runs under <c>nix develop</c>, which exports <c>DEVELOPER_DIR</c>/<c>SDKROOT</c>
    /// pointing at the nix apple-sdk (for VR's own .NET build). A child process that
    /// invokes <c>/usr/bin/git</c> (the macOS xcrun shim) — swival inside nono, the verify
    /// test command — would treat that nix path as the developer directory, find no Command
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
        int cpuSampleIntervalMs = 0,
        Action<ActivityWatchdog.WedgeSample>? onWedgeSample = null,
        Func<bool>? socketProbe = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments);
        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken, environment, killToken, onActivity, envRemove, cpuSampleIntervalMs, onWedgeSample, socketProbe);
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
        int cpuSampleIntervalMs = 0,
        Action<ActivityWatchdog.WedgeSample>? onWedgeSample = null,
        Func<bool>? socketProbe = null)
    {
        var startInfo = new ProcessStartInfo(fileName);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken, environment, killToken, onActivity, envRemove, cpuSampleIntervalMs, onWedgeSample, socketProbe);
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
        int cpuSampleIntervalMs = 0,
        Action<ActivityWatchdog.WedgeSample>? onWedgeSample = null,
        Func<bool>? socketProbe = null)
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
        var cpuTask = cpuSampleIntervalMs > 0 && onActivity is not null
            ? SampleTreeCpuLoopAsync(process.Id, cpuSampleIntervalMs, onActivity, onWedgeSample, socketProbe, cpuCts.Token)
            : Task.CompletedTask;

        try
        {
            // ReSharper disable once AccessToDisposedClosure — killRegistration is
            // disposed (end of this try) strictly before 'process' (end of method),
            // and CancellationTokenRegistration.Dispose() waits for any in-flight
            // callback, so the Kill closure can never run against a disposed process.
            // ReSharper disable once UseAwaitUsing — sync Dispose() is REQUIRED here: it
            // blocks until any running Kill callback finishes (the guarantee above);
            // DisposeAsync() does not provide that synchronous wait.
            using var killRegistration = killToken.CanBeCanceled
                ? killToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already exited */ } })
                : default;

            // Propagate cancellation, mirroring the old WaitForExitAsync(cancellationToken).
            await using var ctReg = cancellationToken.Register(() => exitedTcs.TrySetCanceled(cancellationToken));

            if (timeout != Timeout.InfiniteTimeSpan && await Task.WhenAny(exitedTcs.Task, Task.Delay(timeout, cancellationToken)) != exitedTcs.Task)
            {
                process.Kill(entireProcessTree: true);
                if (stageGroupId.HasValue)
                {
                    try { KillProcessGroup(stageGroupId.Value); } catch { /* best-effort */ }
                }
                lock (outputLock) { return (-1, output.ToString(), true); }
            }

            await exitedTcs.Task;
            // Process exited: REAP FIRST (process-group kill targets this stage's
            // descendants, tree-kill backstops) so survivors release the inherited
            // pipe write-ends, THEN bounded-drain — WaitForExitAsync now EOFs fast.
            if (stageGroupId.HasValue)
                try { KillProcessGroup(stageGroupId.Value); } catch { /* best-effort */ }
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            await Task.WhenAny(process.WaitForExitAsync(CancellationToken.None), Task.Delay(DrainGraceMs, CancellationToken.None));
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
    ///
    /// On every successful sample it ALSO reports a <see cref="ActivityWatchdog.WedgeSample"/>
    /// (agent-subtree-idle ⇔ this window's CPU delta was sub-epsilon, plus the
    /// backend-socket-established verdict from <paramref name="socketProbe"/>) so the
    /// watchdog's additive socket-wedge detector reads a fresh, pid-scoped verdict.
    /// </summary>
    private static async Task SampleTreeCpuLoopAsync(
        int rootPid, int intervalMs, Action<string> onActivity,
        Action<ActivityWatchdog.WedgeSample>? onWedgeSample, Func<bool>? socketProbe, CancellationToken ct)
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

                // Report the wedge verdict: subtree idle ⇔ this window did NOT
                // cross the CPU epsilon. Gated by socketProbe presence so the
                // detector stays inert (no sample emitted) unless wired up.
                if (onWedgeSample is not null && socketProbe is not null)
                    onWedgeSample(new ActivityWatchdog.WedgeSample(
                        SubtreeIdle: !pulse, BackendSocketEstablished: SafeSocketProbe(socketProbe)));

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

    // The socket probe is best-effort: any failure means "no wedge evidence",
    // never a kill — swallow and report false.
    private static bool SafeSocketProbe(Func<bool> socketProbe)
    {
        try { return socketProbe(); }
        catch { return false; }
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
