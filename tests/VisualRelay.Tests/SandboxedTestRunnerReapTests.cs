using System.Diagnostics;
using System.Runtime.InteropServices;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Process-backed regression tests for <see cref="SandboxedTestRunner"/>'s
/// idle-reap watchdog. They use perl "wrapper" scripts that imitate nono
/// supervising a test tree — no real nono needed — and exercise
/// <see cref="SandboxedTestRunner.RunWatchedAsync"/> directly.
/// </summary>
public sealed class SandboxedTestRunnerReapTests
{
    // Reaping (setpgid/kill -pgid) and the perl/ps tooling are POSIX-only.
    private static bool PosixUnsupported =>
        !RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
        !RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // Short windows so the suite stays fast; production uses TestIdleGraceMilliseconds.
    private const int GraceMs = 1_500;
    private const int CpuSampleMs = 200;

    /// <summary>
    /// THE inverted scenario 0c6f184 missed (and the inverse of
    /// ProcessCapture_ReturnsPromptlyWhenChildInheritsPipeAndSurvives): the
    /// directly-spawned WRAPPER stays alive after its child's work is done —
    /// it printed nono's "Command exited with code N" marker, forked an orphan
    /// that inherits the pipe and lingers (testhost/MSBuild), and then sleeps.
    /// The runner must reap on idle and return the inner command's REAL result
    /// promptly, not ride the cap to TimedOut.
    /// </summary>
    [Fact]
    public async Task RunWatchedAsync_WrapperOutlivesFinishedTests_ReturnsRealResultPromptly()
    {
        if (PosixUnsupported) return;
        using var repo = TestRepository.Create();
        var wrapper = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root, "fake-nono-lingers",
            "#!/usr/bin/env perl\n" +
            "$| = 1;\n" +
            "use POSIX; setpgid(0,0);\n" +
            // Orphan inherits stdout/stderr (the lingering testhost/MSBuild node).
            "my $pid = fork();\n" +
            "if ($pid == 0) { exec('sleep', '6'); }\n" +
            "print \"Failed: 1, Passed: 1857\\n\";\n" +
            "print \"Command exited with code 1\\n\";\n" +
            // The wrapper itself lingers, supervising the un-draining tree.
            "sleep 6;\n");

        var sw = Stopwatch.StartNew();
        var result = await SandboxedTestRunner.RunWatchedAsync(
            wrapper, [], repo.Root, null,
            firstOutputTimeoutMs: GraceMs, idleGraceMs: GraceMs,
            hardCap: TimeSpan.FromSeconds(20), cpuSampleIntervalMs: CpuSampleMs,
            CancellationToken.None);
        sw.Stop();

        Assert.False(result.TimedOut,
            $"Reaped run must report a real result, not a timeout. Output:\n{result.Output}");
        Assert.Equal(1, result.ExitCode); // the inner red, from the marker — not the kill signal
        Assert.Contains("Command exited with code 1", result.Output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4),
            $"Expected a prompt idle-reap (~{GraceMs}ms), took {sw.Elapsed.TotalSeconds:F1}s — " +
            "the runner rode the wrapper's lifetime instead of reaping on idle.");
    }

    /// <summary>
    /// A wrapper that finishes and exits cleanly (no lingering tree) still
    /// reports the inner result from the marker, fast — the common case must
    /// not regress.
    /// </summary>
    [Fact]
    public async Task RunWatchedAsync_WrapperExitsCleanly_ReturnsRealResult()
    {
        if (PosixUnsupported) return;
        using var repo = TestRepository.Create();
        var wrapper = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root, "fake-nono-clean",
            "#!/usr/bin/env perl\n$| = 1;\n" +
            "print \"All green\\n\";\n" +
            "print \"Command exited with code 0\\n\";\n" +
            "exit 0;\n");

        var sw = Stopwatch.StartNew();
        var result = await SandboxedTestRunner.RunWatchedAsync(
            wrapper, [], repo.Root, null,
            firstOutputTimeoutMs: GraceMs, idleGraceMs: GraceMs,
            hardCap: TimeSpan.FromSeconds(20), cpuSampleIntervalMs: CpuSampleMs,
            CancellationToken.None);
        sw.Stop();

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4));
    }

    /// <summary>
    /// A wrapper that is silent and idle from the very start (never produces
    /// output or CPU) is a genuine hang: it must still halt (TimedOut), reaped
    /// at the first-output window rather than riding the full cap.
    /// </summary>
    [Fact]
    public async Task RunWatchedAsync_SilentFromStart_HaltsAsTimeout()
    {
        if (PosixUnsupported) return;
        using var repo = TestRepository.Create();
        var wrapper = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root, "fake-nono-silent",
            "#!/usr/bin/env perl\nsleep 30;\n");

        var sw = Stopwatch.StartNew();
        var result = await SandboxedTestRunner.RunWatchedAsync(
            wrapper, [], repo.Root, null,
            firstOutputTimeoutMs: GraceMs, idleGraceMs: GraceMs,
            hardCap: TimeSpan.FromSeconds(6), cpuSampleIntervalMs: CpuSampleMs,
            CancellationToken.None);
        sw.Stop();

        Assert.True(result.TimedOut, "A silent-from-start hang must report as a timeout/halt.");
        Assert.Contains("test command timed out", result.Output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4),
            $"Expected a halt at the first-output window (~{GraceMs}ms), took {sw.Elapsed.TotalSeconds:F1}s.");
    }

    /// <summary>
    /// A wrapper that is CPU-BUSY but output-silent (a long silent compile) must
    /// NOT be reaped — the CPU pulse keeps it alive — so it rides the hard cap
    /// and is reported as a timeout. Guards against false-reaping a working run.
    /// </summary>
    [Fact]
    public async Task RunWatchedAsync_BusyButSilent_NotReaped_RidesHardCap()
    {
        if (PosixUnsupported) return;
        // Needs working process-tree CPU sampling (ps); skip gracefully otherwise.
        if (ProcessTreeCpuSampler.TrySampleTreeCpuMs(Environment.ProcessId) is null)
            return;

        using var repo = TestRepository.Create();
        var wrapper = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root, "fake-nono-busy",
            "#!/usr/bin/env perl\nmy $end = time + 30;\nwhile (time < $end) { }\n");

        var sw = Stopwatch.StartNew();
        var result = await SandboxedTestRunner.RunWatchedAsync(
            wrapper, [], repo.Root, null,
            firstOutputTimeoutMs: GraceMs, idleGraceMs: GraceMs,
            hardCap: TimeSpan.FromSeconds(3), cpuSampleIntervalMs: CpuSampleMs,
            CancellationToken.None);
        sw.Stop();

        Assert.True(result.TimedOut, "A busy-but-silent hang must ride the cap and halt.");
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(2),
            $"Expected the run to ride the ~3s hard cap, but it returned in {sw.Elapsed.TotalSeconds:F1}s — " +
            "the watchdog falsely reaped a CPU-busy process (the CPU pulse should keep it alive).");
    }
}
