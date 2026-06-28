using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Samples cumulative CPU time of a process tree. A filesystem-independent
/// activity signal: a subagent doing real work accrues CPU even when its stdout is
/// quiet and its trace-file view is frozen (virtio-fs attr-cache staleness).
/// Dispatches by OS — a single <c>ps</c> snapshot on Unix, a Toolhelp snapshot plus
/// <see cref="Process.TotalProcessorTime"/> on Windows — and shares one OS-agnostic
/// tree summation. Returns null when sampling fails; callers must treat null as
/// "no signal", never as activity.
/// </summary>
internal static class ProcessTreeCpuSampler
{
    internal static long? TrySampleTreeCpuMs(int rootPid)
    {
        try
        {
            return OperatingSystem.IsWindows() ? SampleWindows(rootPid) : SamplePosix(rootPid);
        }
        catch
        {
            return null;
        }
    }

    // ── POSIX: one `ps` snapshot ─────────────────────────────────────────

    private static long? SamplePosix(int rootPid)
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
            _ = ps.StandardOutput.ReadToEnd();
            return null;
        }

        return ps.ExitCode == 0 ? SumTreeCpuMs(rootPid, stdout) : null;
    }

    private static long SumTreeCpuMs(int rootPid, string psOutput)
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

        return SumTreeCpuMs(rootPid, childrenByParent, cpuByPid);
    }

    // ── Windows: a Toolhelp snapshot + Process.TotalProcessorTime ────────

    private static long? SampleWindows(int rootPid)
    {
        var childrenByParent = BuildWindowsProcessTree();
        if (childrenByParent is null)
            return null;

        // Walk the tree once and sum the per-pid CPU directly — no second traversal.
        long total = 0;
        foreach (var pid in CollectDescendants(rootPid, childrenByParent))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                total += (long)process.TotalProcessorTime.TotalMilliseconds;
            }
            catch (Exception)
            {
                // Process gone or not accessible (e.g. a system pid) — skip it.
            }
        }

        return total;
    }

    private static Dictionary<int, List<int>>? BuildWindowsProcessTree()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snapshot == InvalidHandleValue)
            return null;
        try
        {
            var childrenByParent = new Dictionary<int, List<int>>();
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                return null;
            do
            {
                var pid = (int)entry.th32ProcessID;
                var ppid = (int)entry.th32ParentProcessID;
                if (!childrenByParent.TryGetValue(ppid, out var kids))
                    childrenByParent[ppid] = kids = [];
                kids.Add(pid);
            }
            while (Process32Next(snapshot, ref entry));
            return childrenByParent;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>OS-agnostic tree summation: the cumulative CPU (ms) of
    /// <paramref name="rootPid"/> and every descendant, ignoring unrelated pids.</summary>
    internal static long SumTreeCpuMs(
        int rootPid,
        IReadOnlyDictionary<int, List<int>> childrenByParent,
        IReadOnlyDictionary<int, long> cpuByPid)
    {
        long total = 0;
        foreach (var pid in CollectDescendants(rootPid, childrenByParent))
        {
            if (cpuByPid.TryGetValue(pid, out var cpuMs))
                total += cpuMs;
        }

        return total;
    }

    // ── Toolhelp interop (Windows only; never invoked off the IsWindows branch) ──

    // ReSharper disable once InconsistentNaming — matches Windows API TH32CS_SNAPPROCESS
    private const uint Th32csSnapprocess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    // ReSharper disable once InconsistentNaming — matches Windows API CreateToolhelp32Snapshot
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

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
