using System.Diagnostics;

namespace VisualRelay.Core.Execution;

internal static partial class ProcessCapture
{
    // POSIX signal constant for SIGINT (2), mirroring the existing SIGKILL (9).
    // The kill() P/Invoke already accepts arbitrary signals.
    // ReSharper disable once InconsistentNaming
    private const int SIGINT = 2;

    // Grace window between the polite stop and the hard kill fallback. Matches
    // the proven pattern in BackendLifecycle.StopAsync (SIGTERM → poll → SIGKILL).
    private static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sends SIGINT to the root process and its process group, then polls for
    /// voluntary exit up to <see cref="GraceWindow"/>. If the process is still
    /// alive after the grace window, falls through to the existing hard-kill
    /// path (<c>process.Kill(entireProcessTree: true)</c> +
    /// <c>KillProcessGroup</c>). On Windows the graceful preamble is skipped
    /// and the immediate kill is issued directly, unless a
    /// <paramref name="treeControl"/> is given: then the tree behind the process
    /// (a Linux tree behind wsl.exe) is stopped through the strategy instead, see
    /// <see cref="StopTreeAsync"/>.
    /// </summary>
    /// <remarks>
    /// Both a direct send (<c>kill(pid, SIGINT)</c>) and a process-group send
    /// (<c>kill(-pgid, SIGINT)</c>) are attempted so the signal reaches the
    /// child regardless of whether <c>setpgid</c> took effect before the exec
    /// race. The direct send covers the common case where the child is already
    /// exec'd; the group send reaches descendants through the nono wrapper.
    /// </remarks>
    private static async Task GracefulStopThenKillAsync(
        Process process, int? stageGroupId, TimeProvider tp, IProcessTreeControl? treeControl)
    {
        if (treeControl is not null)
        {
            await StopTreeAsync(treeControl, () => SafeHasExited(process), () => KillLocal(process, stageGroupId), tp);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            return;
        }

        // The processes the command started, read before any signal. The setpgid below the launch
        // loses the race with the child's exec, so the group sends reach no further than the root (see
        // PosixProcessTree). Measured on the Mac: cargo quit on the interrupt inside the grace window,
        // the hard kill was skipped, and a rustc it had started kept compiling after the stop returned.
        var sinceSnapshot = Stopwatch.StartNew();
        var started = stageGroupId is { } rootPid ? PosixProcessTree.Snapshot(rootPid) : [];

        // Send SIGINT to the root process and its process group, and to each process the root
        // started. All best-effort — any of them may have already exited.
        if (stageGroupId.HasValue)
        {
            try { kill(stageGroupId.Value, SIGINT); } catch { /* best-effort */ }
            try { kill(-stageGroupId.Value, SIGINT); } catch { /* best-effort */ }
        }

        foreach (var member in started)
            try { kill(member.Pid, SIGINT); } catch { /* best-effort */ }

        await WaitForVoluntaryExitAsync(
            () => SafeHasExited(process) && started.All(member => kill(member.Pid, 0) != 0), tp);

        // Still alive after grace — hard kill.
        if (!SafeHasExited(process))
            KillLocal(process, stageGroupId);

        foreach (var pid in PosixProcessTree.StillRunning(started, sinceSnapshot.Elapsed))
            try { kill(pid, SIGKILL); } catch { /* best-effort */ }
    }

    /// <summary>
    /// The reap step for a tree the host cannot see. On POSIX the process-group kill
    /// takes the stage's descendants with it when the child exits; behind wsl.exe
    /// there is no group and no visible tree, so nothing reaped the Linux side after
    /// an ordinary exit and a survivor could hold the workspace. Best-effort by
    /// construction: the tree is usually already gone, and a control that fails must
    /// never turn a finished run into a failed one.
    /// </summary>
    /// <param name="treeControl">The control, or null when the host sees the tree.</param>
    /// <returns>The completed reap.</returns>
    internal static Task ReapTreeAsync(IProcessTreeControl? treeControl) =>
        treeControl is null ? Task.CompletedTask : SafeStopAsync(treeControl, graceful: false);

    /// <summary>
    /// The stop sequence for a tree controlled through a strategy: ask the tree to
    /// stop, wait the grace window for the local process (which exits with its
    /// tree) to go, then force the tree and kill the local process. A failing
    /// strategy never aborts the sequence: the local kill always follows.
    /// Internal so the sequence is asserted under a virtual clock without a process.
    /// </summary>
    internal static async Task StopTreeAsync(
        IProcessTreeControl control, Func<bool> hasExited, Action killLocal, TimeProvider tp)
    {
        await SafeStopAsync(control, graceful: true);
        await WaitForVoluntaryExitAsync(hasExited, tp);
        if (hasExited())
            return;

        await SafeStopAsync(control, graceful: false);
        try { killLocal(); } catch { /* already exited */ }
    }

    // Poll for voluntary exit every 200 ms (mirrors BackendLifecycle) up to the
    // grace window. Every hasExited call is guarded by the caller: when called
    // from the killToken.Register fire-and-forget callback the Process may have
    // been disposed before this background task completes.
    private static async Task WaitForVoluntaryExitAsync(Func<bool> hasExited, TimeProvider tp)
    {
        var deadline = tp.GetUtcNow() + GraceWindow;
        while (tp.GetUtcNow() < deadline && !hasExited())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), tp);
        }
    }

    private static async Task SafeStopAsync(IProcessTreeControl control, bool graceful)
    {
        try { await control.StopAsync(graceful, CancellationToken.None); }
        catch { /* best-effort: the local kill still follows */ }
    }

    // The hard kill of the local process and (POSIX) its process group.
    private static void KillLocal(Process process, int? stageGroupId)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        if (stageGroupId.HasValue)
        {
            try { KillProcessGroup(stageGroupId.Value); } catch { /* best-effort */ }
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
