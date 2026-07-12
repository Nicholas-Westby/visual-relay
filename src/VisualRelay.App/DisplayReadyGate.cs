using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VisualRelay.App;

/// <summary>
/// macOS display-sleep gate: waits for an active display before Avalonia
/// initialization so the CoreVideo-backed render timer can start.
///
/// Avalonia's macOS render timer is a CVDisplayLink. When every display is
/// asleep, <c>CVDisplayLinkCreateWithActiveCGDisplays</c> returns
/// <c>kCVReturnInvalidArgument</c> (-6661), and <c>AppBuilder.Setup()</c>
/// crashes with <c>InvalidOperationException</c>. This gate probes that
/// exact CoreVideo call, wakes the display via <c>caffeinate</c>, and
/// retries until a display link can be created or the attempt budget is
/// exhausted.
///
/// It is a complete no-op off macOS. On exhaustion it lets startup proceed
/// so the existing loud crash is preserved — the gate must never convert a
/// hard failure into a silent hang or swallowed error.
/// </summary>
internal static class DisplayReadyGate
{
    private static readonly TimeSpan DelayBetweenAttempts = TimeSpan.FromSeconds(5);
    private const int MaxAttempts = 24;

    // ── Public entry point ────────────────────────────────────────────────

    /// <summary>
    /// On macOS blocks until a display link can be created (or the retry
    /// budget is exhausted), waking the display to make that happen
    /// promptly. Off macOS this is a no-op.
    /// </summary>
    public static void EnsureDisplayReady()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var retryRound = 0;
        var ready = WaitUntilReady(
            probe: ProbeDisplayLink,
            wake: WakeDisplay,
            delay: () =>
            {
                retryRound++;
                Console.Error.WriteLine(
                    "[DisplayReadyGate] Display still asleep — retry round {0}/{1}...",
                    retryRound, MaxAttempts - 1);
                Thread.Sleep(DelayBetweenAttempts);
            },
            maxAttempts: MaxAttempts);

        if (!ready)
        {
            Console.Error.WriteLine(
                "[DisplayReadyGate] Gave up after {0} attempts — " +
                "proceeding into Avalonia (crash likely if display is still asleep).",
                MaxAttempts);
        }
    }

    // ── Testable core ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the display became ready within
    /// the attempt budget (or was ready immediately);
    /// <see langword="false"/> when the budget was exhausted.
    /// <paramref name="probe"/> returns a CoreVideo result code
    /// (0 = success); <paramref name="wake"/> requests a display wake;
    /// <paramref name="delay"/> sleeps between attempts.
    /// </summary>
    internal static bool WaitUntilReady(
        Func<int> probe, Action wake, Action delay, int maxAttempts)
    {
        if (probe() == 0)
            return true;

        for (var attempt = 1; attempt < maxAttempts; attempt++)
        {
            try
            {
                wake();
            }
            catch
            {
                // Best-effort: wake failure must not abort the loop.
            }

            delay();

            if (probe() == 0)
                return true;
        }

        return false;
    }

    // ── Real adapters ─────────────────────────────────────────────────────

    /// <summary>
    /// Tries to create and immediately release a CVDisplayLink. Returns the
    /// native result code (0 = success, -6661 = no active display, etc.).
    /// </summary>
    private static int ProbeDisplayLink()
    {
        var result = CVDisplayLinkCreateWithActiveCGDisplays(out var link);
        if (result == 0 && link != IntPtr.Zero)
            CVDisplayLinkRelease(link);
        return result;
    }

    /// <summary>
    /// Starts <c>/usr/bin/caffeinate -u -t 5</c> to declare user activity
    /// and wake the display. Best-effort: launch failures are swallowed,
    /// the process is not waited for.
    /// </summary>
    private static void WakeDisplay()
    {
        try
        {
            Process.Start("/usr/bin/caffeinate", "-u -t 5");
        }
        catch
        {
            // Best-effort: never let a caffeinate failure abort the gate.
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────

    [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
    private static extern int CVDisplayLinkCreateWithActiveCGDisplays(
        out IntPtr displayLink);

    [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
    private static extern void CVDisplayLinkRelease(IntPtr displayLink);
}
