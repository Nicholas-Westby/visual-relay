using System.Diagnostics;
using System.Globalization;

namespace VisualRelay.Core.Execution;

/// <summary>
/// The processes below a command's root, read from <c>ps</c>, for stopping a command whose children
/// never joined a process group of their own. VR's setpgid on each child races the child's exec and
/// loses: measured on the Mac, 10 of 10 launches stayed in VR's own group, so a signal sent to the
/// command's group reached nothing beyond the root.
/// </summary>
internal static class PosixProcessTree
{
    /// <summary>A process below the root, and how long it had been running when it was read.</summary>
    internal readonly record struct Member(int Pid, long ElapsedMs);

    /// <summary>The processes below <paramref name="rootPid"/> now; empty when ps cannot be read.</summary>
    internal static IReadOnlyList<Member> Snapshot(int rootPid) =>
        ReadTable() is { } table ? DescendantsOf(rootPid, table) : [];

    /// <summary>
    /// The pids of <paramref name="members"/> still running as the same processes,
    /// <paramref name="sinceSnapshot"/> after they were read; empty when ps cannot be read.
    /// </summary>
    internal static IReadOnlyList<int> StillRunning(IReadOnlyList<Member> members, TimeSpan sinceSnapshot) =>
        members.Count == 0 || ReadTable() is not { } table ? [] : StillRunning(members, sinceSnapshot, table);

    /// <summary>The processes below <paramref name="rootPid"/> in a headerless <c>ps -axo pid=,ppid=,etime=</c>.</summary>
    internal static IReadOnlyList<Member> DescendantsOf(int rootPid, string psOutput)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        var elapsedByPid = new Dictionary<int, long>();
        foreach (var (pid, ppid, elapsedMs) in Rows(psOutput))
        {
            elapsedByPid[pid] = elapsedMs;
            if (!childrenByParent.TryGetValue(ppid, out var siblings))
                childrenByParent[ppid] = siblings = [];
            siblings.Add(pid);
        }

        return [.. ProcessTreeCpuSampler.CollectDescendants(rootPid, childrenByParent).Skip(1)
            .Select(pid => new Member(pid, elapsedByPid[pid]))];
    }

    /// <summary>
    /// Of <paramref name="members"/>, the pids a later table still lists as the same processes. A pid
    /// counts only if its process aged with the clock: a process that reused the number started after
    /// the members were read, so it is younger. ps shows whole seconds, hence the two seconds of slack.
    /// </summary>
    internal static IReadOnlyList<int> StillRunning(IReadOnlyList<Member> members, TimeSpan sinceSnapshot, string psOutput)
    {
        var elapsedNow = new Dictionary<int, long>();
        foreach (var (pid, _, elapsedMs) in Rows(psOutput))
            elapsedNow[pid] = elapsedMs;

        var aged = (long)sinceSnapshot.TotalMilliseconds - 2_000;
        return [.. members
            .Where(member => elapsedNow.TryGetValue(member.Pid, out var now) && now >= member.ElapsedMs + aged)
            .Select(member => member.Pid)];
    }

    private static IEnumerable<(int Pid, int Ppid, long ElapsedMs)> Rows(string psOutput)
    {
        foreach (var line in psOutput.Split('\n'))
        {
            var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 3
                && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid)
                && ProcessTreeCpuSampler.ParseCpuTimeMs(fields[2]) is >= 0 and var elapsedMs)
                yield return (pid, ppid, elapsedMs);
        }
    }

    private static string? ReadTable()
    {
        try
        {
            using var ps = new Process();
            ps.StartInfo = new ProcessStartInfo("/bin/ps", "-axo pid=,ppid=,etime=")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            ps.Start();
            var stdout = ps.StandardOutput.ReadToEnd();
            if (ps.WaitForExit(2_000))
                return ps.ExitCode == 0 ? stdout : null;

            try { ps.Kill(entireProcessTree: true); } catch { /* gone */ }
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
