using System.Diagnostics;
using System.Globalization;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Samples cumulative CPU time of a process tree via one `ps` snapshot.
/// A filesystem-independent activity signal: a subagent doing real work
/// accrues CPU even when its stdout is quiet and its trace-file view is
/// frozen (virtio-fs attr-cache staleness). Returns null when sampling
/// fails — callers must treat null as "no signal", never as activity.
/// </summary>
internal static class ProcessTreeCpuSampler
{
    internal static long? TrySampleTreeCpuMs(int rootPid)
    {
        try
        {
            using var ps = new Process();
            ps.StartInfo = new ProcessStartInfo("/bin/ps", "-axo pid=,ppid=,time=")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            ps.Start();
            var stdout = ps.StandardOutput.ReadToEnd();
            if (!ps.WaitForExit(2_000))
            {
                try { ps.Kill(entireProcessTree: true); } catch { /* gone */ }
                return null;
            }

            return ps.ExitCode == 0 ? SumTreeCpuMs(rootPid, stdout) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static long SumTreeCpuMs(int rootPid, string psOutput)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        var cpuByPid = new Dictionary<int, long>();
        foreach (var rawLine in psOutput.Split('\n'))
        {
            var fields = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3)
                continue;
            if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid))
                continue;

            var cpuMs = ParseCpuTimeMs(fields[2]);
            if (cpuMs >= 0)
                cpuByPid[pid] = cpuMs;

            if (!childrenByParent.TryGetValue(ppid, out var siblings))
                childrenByParent[ppid] = siblings = [];
            siblings.Add(pid);
        }

        long total = 0;
        foreach (var pid in CollectDescendants(rootPid, childrenByParent))
        {
            if (cpuByPid.TryGetValue(pid, out var cpuMs))
                total += cpuMs;
        }

        return total;
    }

    internal static IReadOnlyList<int> CollectDescendants(
        int root, IReadOnlyDictionary<int, List<int>> childrenByParent)
    {
        var seen = new HashSet<int> { root };
        var result = new List<int> { root };
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            if (!childrenByParent.TryGetValue(queue.Dequeue(), out var kids))
                continue;
            foreach (var kid in kids)
            {
                if (seen.Add(kid))
                {
                    result.Add(kid);
                    queue.Enqueue(kid);
                }
            }
        }

        return result;
    }

    /// <summary>Parses ps TIME values: [dd-][hh:]mm:ss[.cc]. Returns -1 when invalid.</summary>
    internal static long ParseCpuTimeMs(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return -1;

        var rest = value.Trim();
        long days = 0;
        var dash = rest.IndexOf('-');
        if (dash >= 0)
        {
            if (!long.TryParse(rest[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out days))
                return -1;
            rest = rest[(dash + 1)..];
        }

        var parts = rest.Split(':');
        if (parts.Length is < 2 or > 3)
            return -1;

        long hours = 0;
        var index = 0;
        if (parts.Length == 3)
        {
            if (!long.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
                return -1;
            index++;
        }

        if (!long.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            return -1;
        index++;

        if (!decimal.TryParse(parts[index], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0)
            return -1;

        var wholeMs = ((days * 24 + hours) * 60 + minutes) * 60_000;
        return wholeMs + (long)(seconds * 1_000);
    }
}
