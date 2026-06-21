using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VisualRelay.Cli;

/// <summary>
/// Wall-clock timeout watchdog. Runs a child with inherited stdio; if it
/// outlives the supplied timeout the whole process tree is killed and 124
/// is returned (the GNU <c>timeout</c> convention). Replaces the launcher's bash
/// <c>_timeout_watchdog</c>: like that version it escalates SIGTERM→SIGKILL and
/// reports 124 even when the child catches SIGTERM and exits 0 (dotnet does).
/// On Unix the child is placed in its own process group (via <c>setpgid</c>) so
/// the kill reaps the entire tree (dotnet → testhost → tests).
/// </summary>
public static class TimeoutWatchdog
{
    public static async Task<int> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        if (extraEnv is not null)
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {fileName}");

        var unix = !OperatingSystem.IsWindows();
        if (unix)
        {
            // Put the child in its own process group (leader == child) so a
            // group kill tears down the whole tree. Best-effort: the child may
            // have already exited, in which case the tree-kill fallback applies.
            try { _ = SetPgid(process.Id, process.Id); } catch (Exception) { /* race / unsupported */ }
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            KillTree(process, unix);
            return 124;
        }
    }

    private static void KillTree(Process process, bool unix)
    {
        // Primary: let the runtime reap the child and the descendants it tracks.
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Fall through to the group kill below.
        }

        // Belt-and-braces on Unix: also signal the child's process GROUP so any
        // grandchild the runtime missed dies too. Guarded — only when the child
        // became the LEADER of its own group (pgid == child pid). That proves the
        // group is the child's alone, so kill(-pgid) can never reach this process
        // or the test host (a critical safety property under `dotnet test`).
        if (unix)
        {
            var pgid = GetPgid(process.Id);
            if (pgid == process.Id && pgid != GetPgid(0))
            {
                _ = Kill(-pgid, Sigterm);
                Thread.Sleep(200);
                _ = Kill(-pgid, Sigkill);
            }
        }
    }

    private const int Sigterm = 15;
    private const int Sigkill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);

    [DllImport("libc", SetLastError = true, EntryPoint = "setpgid")]
    private static extern int setpgid(int pid, int pgid);

    private static int Kill(int pid, int sig) => kill(pid, sig);

    private static int GetPgid(int pid) => getpgid(pid);

    private static int SetPgid(int pid, int pgid) => setpgid(pid, pgid);
}
