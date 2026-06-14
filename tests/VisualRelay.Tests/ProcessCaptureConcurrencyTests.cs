using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Regression tests for the <see cref="ProcessCapture"/> stdout/stderr
/// concurrency bug fixed in commit da5116e.
///
/// Root cause: <c>OutputDataReceived</c> and <c>ErrorDataReceived</c> fire on
/// separate thread-pool threads and both called <c>StringBuilder.AppendLine</c>
/// on the shared <c>output</c> buffer.  <c>StringBuilder</c> is not
/// thread-safe; heavy interleaved writes corrupted internal state and threw
/// <c>ArgumentException: Destination is too short</c>, crashing the entire
/// drain.  The fix wraps every append and the final <c>ToString()</c> read
/// in a shared <c>lock</c>.
///
/// Each theory iteration spawns a shell that emits 8 000 interleaved
/// stdout/stderr lines as fast as possible.  Without the lock the race
/// would corrupt the buffer on the first few runs; with the lock all
/// iterations must complete cleanly.
/// </summary>
public sealed class ProcessCaptureConcurrencyTests
{
    // Number of interleaved line pairs emitted by the child process.
    // 8 000 pairs = 16 000 lines; enough to reliably hit the race in the
    // unfixed code while keeping the test fast (well under 10 s per run).
    private const int LineCount = 8_000;

    // Generous wall-clock budget per RunAsync call — the operation itself
    // takes ~1–2 s on a typical dev machine; 60 s leaves ample headroom.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    // Shell command: alternate stdout/stderr lines so the two I/O threads
    // interleave as densely as possible.
    private static string ShellCommand =>
        $"for i in $(seq 1 {LineCount}); do echo out$i; echo err$i 1>&2; done";

    // ── Theory runs the capture three times to raise the probability of ──
    // ── catching the race if the lock were absent.                       ──

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ConcurrentStdoutStderr_DoesNotCorruptOutput(int run)
    {
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            "/bin/sh",
            $"-c \"{ShellCommand}\"",
            Directory.GetCurrentDirectory(),
            RunTimeout,
            CancellationToken.None);

        // ── Structural assertions ────────────────────────────────────────

        Assert.False(timedOut, $"Run {run}: Process should not time out");
        Assert.Equal(0, exitCode);
        Assert.NotNull(output);
        Assert.NotEmpty(output);

        // ── Content assertions ───────────────────────────────────────────
        // The output must contain the first and last lines of both streams.

        Assert.Contains("out1", output, StringComparison.Ordinal);
        Assert.Contains($"out{LineCount}", output, StringComparison.Ordinal);
        Assert.Contains("err1", output, StringComparison.Ordinal);
        Assert.Contains($"err{LineCount}", output, StringComparison.Ordinal);

        // ── Volume assertion ─────────────────────────────────────────────
        // Each of the LineCount iterations writes one stdout line and one
        // stderr line, so the combined output must have at least 2×LineCount
        // newline-delimited entries.  Allow a 10 % tolerance to accommodate
        // any buffered trailing newline differences.
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var expectedMin = (int)(LineCount * 2 * 0.9);
        Assert.True(
            lines.Length >= expectedMin,
            $"Run {run}: Expected at least {expectedMin} lines (90 % of {LineCount * 2}), got {lines.Length}. "
          + "This suggests line loss or buffer corruption.");
    }
}
