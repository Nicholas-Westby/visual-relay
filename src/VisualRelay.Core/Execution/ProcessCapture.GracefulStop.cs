using System.Diagnostics;

namespace VisualRelay.Core.Execution;

internal static partial class ProcessCapture
{
    // POSIX signal constant for SIGINT (2), mirroring the existing SIGKILL (9).
    // The kill() P/Invoke already accepts arbitrary signals.
    // ReSharper disable once InconsistentNaming
    private const int SIGINT = 2;

    // Grace window between SIGINT and the hard SIGKILL fallback. Matches the
    // proven pattern in BackendLifecycle.StopAsync (SIGTERM → poll → SIGKILL).
    private static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sends SIGINT to the root process and its process group, then polls for
    /// voluntary exit up to <see cref="GraceWindow"/>. If the process is still
    /// alive after the grace window, falls through to the existing hard-kill
    /// path (<c>process.Kill(entireProcessTree: true)</c> +
    /// <c>KillProcessGroup</c>). On Windows the graceful preamble is skipped
    /// and the immediate kill is issued directly.
    /// </summary>
    /// <remarks>
    /// Both a direct send (<c>kill(pid, SIGINT)</c>) and a process-group send
    /// (<c>kill(-pgid, SIGINT)</c>) are attempted so the signal reaches the
    /// child regardless of whether <c>setpgid</c> took effect before the exec
    /// race. The direct send covers the common case where the child is already
    /// exec'd; the group send reaches descendants through the nono wrapper.
    /// </remarks>
    private static async Task GracefulStopThenKillAsync(Process process, int? stageGroupId)
    {
        if (OperatingSystem.IsWindows())
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            return;
        }

        // Send SIGINT to the root process and its process group. Both are
        // best-effort — the child may have already exited.
        if (stageGroupId.HasValue)
        {
            try { kill(stageGroupId.Value, SIGINT); } catch { /* best-effort */ }
            try { kill(-stageGroupId.Value, SIGINT); } catch { /* best-effort */ }
        }

        // Poll for voluntary exit every 200 ms (mirrors BackendLifecycle).
        // Every process.HasExited access is guarded: when called from the
        // killToken.Register fire-and-forget callback the Process may have
        // been disposed before this background task completes.
        var deadline = DateTime.UtcNow + GraceWindow;
        while (DateTime.UtcNow < deadline && !SafeHasExited(process))
        {
            await Task.Delay(200);
        }

        // Still alive after grace — hard kill.
        if (!SafeHasExited(process))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            if (stageGroupId.HasValue)
            {
                try { KillProcessGroup(stageGroupId.Value); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Returns <c>process.HasExited</c>, treating any exception (including
    /// <see cref="ObjectDisposedException"/> from a fire-and-forget race) as
    /// "already exited" so the graceful-stop task never throws unobserved.
    /// </summary>
    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }
}
