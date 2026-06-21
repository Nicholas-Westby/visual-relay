using System.Runtime.InteropServices;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Pidfile and POSIX-signal helpers for the backend proxy: the C# counterpart of
/// the bash <c>live_pid</c> / <c>kill</c> plumbing. A pidfile whose process is
/// gone (a <c>kill -0</c> probe fails) is treated as stale and reported as
/// <c>null</c>, matching the script's stale-pid safety. SIGTERM (15) then SIGKILL
/// (9) drive graceful-then-forced stop.
/// </summary>
public static class BackendProcess
{
    private const int Sig0Probe = 0;   // existence/permission probe, sends no signal
    private const int Sigterm = 15;
    private const int Sigkill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    // ESRCH: no such process. EPERM: process exists but we can't signal it — still
    // "alive" for liveness purposes, matching `kill -0`'s exit-0 in that case.
    private const int Esrch = 3;

    /// <summary>
    /// True when <paramref name="pid"/> names a live process this user can see —
    /// the C# equivalent of <c>kill -0 "$pid"</c>. EPERM (process exists, not
    /// ours to signal) still counts as alive; only ESRCH means gone.
    /// </summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
            return false;
        if (kill(pid, Sig0Probe) == 0)
            return true;
        return Marshal.GetLastPInvokeError() != Esrch;
    }

    /// <summary>Sends SIGTERM to <paramref name="pid"/>; best-effort (ignores errno).</summary>
    public static void SendTerm(int pid) => _ = kill(pid, Sigterm);

    /// <summary>Sends SIGKILL to <paramref name="pid"/>; best-effort (ignores errno).</summary>
    public static void SendKill(int pid) => _ = kill(pid, Sigkill);

    /// <summary>
    /// Reads a <em>live</em> pid from <paramref name="pidFile"/>, or <c>null</c>.
    /// A missing/empty/unparseable file, or one whose process is gone (per
    /// <see cref="IsAlive"/>), all resolve to <c>null</c> — the bash
    /// <c>live_pid</c> contract. The liveness predicate is injectable so unit
    /// tests can drive stale-vs-live without spawning a process.
    /// </summary>
    public static int? ReadLivePid(string pidFile, Func<int, bool>? isAlive = null)
    {
        isAlive ??= IsAlive;
        if (!File.Exists(pidFile))
            return null;
        string text;
        try
        {
            text = File.ReadAllText(pidFile).Trim();
        }
        catch (IOException)
        {
            return null;
        }
        if (!int.TryParse(text, out var pid) || pid <= 0)
            return null;
        return isAlive(pid) ? pid : null;
    }

    /// <summary>Best-effort delete of the pidfile; never throws.</summary>
    public static void RemovePidFile(string pidFile)
    {
        try
        {
            File.Delete(pidFile);
        }
        catch (IOException)
        {
            // best-effort: a concurrent stop already removed it.
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort.
        }
    }
}
