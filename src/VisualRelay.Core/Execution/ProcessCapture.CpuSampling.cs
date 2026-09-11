namespace VisualRelay.Core.Execution;

internal static partial class ProcessCapture
{
    // CPU delta per sample window that counts as real work rather than
    // scheduler dust from an idle-blocked process.
    private const long CpuPulseEpsilonMs = 50;

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

    /// <summary>The host-visible sampler: <c>ps</c> on Unix, Toolhelp on Windows (see <see cref="ProcessTreeCpuSampler"/>).</summary>
    private static Func<CancellationToken, Task<long?>> HostTreeSampler(int rootPid) =>
        _ => Task.FromResult(ProcessTreeCpuSampler.TrySampleTreeCpuMs(rootPid));

    /// <summary>
    /// Pulses onActivity("cpu") whenever the process tree accrues CPU between
    /// samples — the one activity signal the target repo's filesystem cannot
    /// freeze. <paramref name="sample"/> is the host's own sampler, or the
    /// injected <see cref="IProcessTreeControl"/>'s when the tree lives where the
    /// host cannot see it. Sampling failures (null return) invalidate the baseline
    /// so accumulated CPU during a failure gap can never cross the epsilon and
    /// emit a spurious pulse. The next successful sample silently
    /// re-establishes the baseline without signalling.
    ///
    /// On every successful sample it ALSO reports a <see cref="ActivityWatchdog.WedgeSample"/>
    /// (agent-subtree-idle ⇔ this window's CPU delta was sub-epsilon, plus the
    /// backend-socket-established verdict from <paramref name="socketProbe"/>) so the
    /// watchdog's additive socket-wedge detector reads a fresh, pid-scoped verdict.
    /// </summary>
    internal static async Task SampleTreeCpuLoopAsync(
        Func<CancellationToken, Task<long?>> sample, int intervalMs, Action<string> onActivity,
        Action<ActivityWatchdog.WedgeSample>? onWedgeSample, Func<bool>? socketProbe, TimeProvider tp, CancellationToken ct)
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
                await Task.Delay(TimeSpan.FromMilliseconds(intervalMs), tp, ct);
                var sampled = await sample(ct);
                if (sampled is null)
                {
                    baseline = null;
                    continue;
                }
                var (pulse, newBaseline) = TryDecideCpuPulse(baseline, sampled.Value, CpuPulseEpsilonMs);
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
}
