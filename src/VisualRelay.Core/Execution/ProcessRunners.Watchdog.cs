using System.Diagnostics;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Polls the trace directory for first-output liveness.  When no trace entry or
/// file appears within <c>timeoutMs</c>, cancels the <c>kill</c> source so the
/// caller can kill the swival process and retry.  Disarms as soon as ANY file
/// entry exists in <c>traceDir</c> — the first trace entry is the signal that
/// the upstream model-backend call has started streaming.
/// </summary>
internal static class FirstOutputWatchdog
{
    /// <summary>
    /// Polls <paramref name="traceDir"/> every 200 ms.
    /// Returns <c>false</c> when a file appears (first output — disarmed) or
    /// <paramref name="ct"/> is cancelled.
    /// Returns <c>true</c> when <paramref name="timeoutMs"/> elapses with no
    /// output — the caller must kill the process and either retry or flag the
    /// stall.  The <paramref name="kill"/> source is cancelled before returning
    /// <c>true</c> so any in-flight ProcessCapture reacts.
    /// </summary>
    public static async Task<bool> WaitAsync(
        string traceDir,
        int timeoutMs,
        CancellationTokenSource kill,
        CancellationToken ct)
    {
        var deadline = TimeSpan.FromMilliseconds(timeoutMs);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (ct.IsCancellationRequested)
                return false;
            if (Directory.Exists(traceDir) && Directory.EnumerateFileSystemEntries(traceDir).Any())
                return false; // first output detected — disarm

            var remaining = deadline - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            // Bound the last sleep to the remaining time so we never overshoot.
            var delay = remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
        }

        // Final check: a trace entry may have been written during the last sleep.
        if (ct.IsCancellationRequested)
            return false;
        if (Directory.Exists(traceDir) && Directory.EnumerateFileSystemEntries(traceDir).Any())
            return false;

        // No output appeared within the per-tier threshold.
        if (!ct.IsCancellationRequested)
            kill.Cancel();
        return true; // fired — caller must kill + retry
    }
}
