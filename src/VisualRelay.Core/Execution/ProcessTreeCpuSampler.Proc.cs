using System.Globalization;
using System.Runtime.InteropServices;

namespace VisualRelay.Core.Execution;

// Linux: CPU time from /proc/<pid>/stat, in clock ticks. procps' ps prints TIME in whole
// seconds, so a quiet but live tree showed no growth for most of a minute and was reaped as
// idle: measured in WSL with commons-lang, the ps sum stayed at 239 s for 45 s of a surefire
// run while the same pids' utime and stime rose 860 ms.
internal static partial class ProcessTreeCpuSampler
{
    /// <summary>
    /// A POSIX sh script printing every process's stat line, then <c>hz &lt;clock ticks per
    /// second&gt;</c>. Processes that exit mid-read are skipped, and the exit code stays 0.
    /// </summary>
    internal const string ProcStatScript =
        "cat /proc/[0-9]*/stat 2>/dev/null; echo \"hz $(getconf CLK_TCK 2>/dev/null || echo 100)\"";

    private const long DefaultTicksPerSecond = 100;

    // ReSharper disable once InconsistentNaming — the Linux sysconf name from <unistd.h>
    private const int _SC_CLK_TCK = 2;

    /// <summary>
    /// Sums the tree of <paramref name="rootPid"/> from <see cref="ProcStatScript"/>'s output:
    /// each process's user and system time plus that of the children it has reaped, so the
    /// CPU of a finished compiler still counts once its parent has collected it.
    /// </summary>
    internal static long SumProcStatTreeCpuMs(int rootPid, string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var ticksPerSecond = lines
            .Where(l => l.StartsWith("hz ", StringComparison.Ordinal))
            .Select(l => long.TryParse(l[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var hz) && hz > 0 ? hz : 0)
            .LastOrDefault(hz => hz > 0);
        return SumTree(rootPid, lines, ticksPerSecond > 0 ? ticksPerSecond : DefaultTicksPerSecond);
    }

    /// <summary>
    /// Pid, parent pid and CPU (ms) from one stat line, or null when it is not one. Field 2 is
    /// the command name in parentheses and may hold spaces and parentheses, so the fields are
    /// counted from the last closing parenthesis: utime, stime, cutime and cstime are 14 to 17.
    /// </summary>
    internal static (int Pid, int ParentPid, long CpuMs)? ParseProcStat(string line, long ticksPerSecond)
    {
        var firstSpace = line.IndexOf(' ');
        var close = line.LastIndexOf(')');
        if (firstSpace <= 0 || close < firstSpace
            || !int.TryParse(line[..firstSpace], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            return null;

        var fields = line[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 15
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid))
            return null;

        long ticks = 0;
        for (var i = 11; i <= 14; i++)
        {
            if (!long.TryParse(fields[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                return null;
            ticks += value;
        }

        return (pid, parentPid, ticks * 1_000 / ticksPerSecond);
    }

    /// <summary>This host's own /proc, read in process: the native Linux counterpart of the WSL sample.</summary>
    private static long? SampleProc(int rootPid)
    {
        var lines = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                continue;
            try
            {
                lines.Add(File.ReadAllText(Path.Combine(dir, "stat")).Trim());
            }
            catch (Exception)
            {
                // The process exited between the listing and the read.
            }
        }

        var ticksPerSecond = sysconf(_SC_CLK_TCK);
        return SumTree(rootPid, lines, ticksPerSecond > 0 ? ticksPerSecond : DefaultTicksPerSecond);
    }

    private static long SumTree(int rootPid, IEnumerable<string> lines, long ticksPerSecond)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        var cpuByPid = new Dictionary<int, long>();
        foreach (var line in lines)
        {
            if (ParseProcStat(line, ticksPerSecond) is not { } stat)
                continue;
            cpuByPid[stat.Pid] = stat.CpuMs;
            if (!childrenByParent.TryGetValue(stat.ParentPid, out var siblings))
                childrenByParent[stat.ParentPid] = siblings = [];
            siblings.Add(stat.Pid);
        }

        return SumTreeCpuMs(rootPid, childrenByParent, cpuByPid);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern long sysconf(int name);
}
