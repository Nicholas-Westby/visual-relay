using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// CPU time on Linux comes from <c>/proc/&lt;pid&gt;/stat</c>, in clock ticks. procps' <c>ps</c>
/// prints TIME in whole seconds, so a quiet but live tree showed no growth for most of a minute
/// and was reaped as idle. Measured in WSL with apache/commons-lang: the ps sum stayed at 239 s
/// for 45 s of a surefire run while the same pids' utime and stime rose by 86 ticks (860 ms),
/// and both tasks' test runs were flagged "CPU-idle" inside LockingVisitorsTest.
/// </summary>
public sealed class ProcessTreeCpuSamplerProcStatTests
{
    /// <summary>A stat line; field 2 holds the command name in parentheses, spaces and parentheses included.</summary>
    private static string Stat(int pid, string comm, int ppid, long utime, long stime, long cutime = 0, long cstime = 0) =>
        $"{pid} ({comm}) S {ppid} {pid} {ppid} 0 -1 4194560 100 0 0 0 {utime} {stime} {cutime} {cstime} 20 0 30 0 1 0 0 12345 678";

    [Fact]
    public void ParseProcStat_ReadsPidParentAndCpu_WhateverTheCommandNameHolds()
    {
        var stat = ProcessTreeCpuSampler.ParseProcStat(
            Stat(4242, "java (pool) 1", 4200, utime: 150, stime: 50, cutime: 7, cstime: 3), ticksPerSecond: 100);

        Assert.Equal((4242, 4200, 2_100L), stat);
    }

    [Fact]
    public void LessThanASecondOfWork_ShowsUpAsGrowth()
    {
        string Snapshot(long utime) =>
            string.Join('\n', Stat(3804, "nono", 1, 1, 1), Stat(3879, "java", 3804, utime, 400), Stat(5000, "cron", 1, 90, 10))
            + "\nhz 100\n";

        var before = ProcessTreeCpuSampler.SumProcStatTreeCpuMs(3804, Snapshot(utime: 23_534));
        var after = ProcessTreeCpuSampler.SumProcStatTreeCpuMs(3804, Snapshot(utime: 23_620));

        Assert.Equal(860, after - before);
    }

    [Fact]
    public void AReapedChildsCpu_StillCounts()
    {
        var withChild = ProcessTreeCpuSampler.SumProcStatTreeCpuMs(10,
            string.Join('\n', Stat(10, "sh", 1, 5, 5), Stat(11, "cc1plus", 10, 300, 100)) + "\nhz 100\n");
        var afterReap = ProcessTreeCpuSampler.SumProcStatTreeCpuMs(10,
            Stat(10, "sh", 1, 5, 5, cutime: 300, cstime: 100) + "\nhz 100\n");

        Assert.Equal(withChild, afterReap);
    }

    [Fact]
    public void TheWslSample_ReadsProcStatInsteadOfPs()
    {
        var argv = WslProcessControl.SampleArgv();

        Assert.DoesNotContain("ps", argv);
        Assert.Contains(argv, a => a.Contains("/proc/", StringComparison.Ordinal));
    }

    /// <summary>The script the distro runs, run for real where there is a /proc: this process's own CPU shows up.</summary>
    [Fact]
    public async Task TheSampleScript_OnLinux_SeesThisProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");
        var argv = WslProcessControl.SampleArgv();

        var (exitCode, output, _) = await ProcessCapture.RunAsync(
            argv[0], argv.Skip(1), Path.GetTempPath(), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(ProcessTreeCpuSampler.SumProcStatTreeCpuMs(Environment.ProcessId, output) > 0);
    }
}
