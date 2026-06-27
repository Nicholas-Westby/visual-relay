using System.Diagnostics;
using System.Runtime.InteropServices;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Regression tests for file-descriptor leaks across subprocess spawns.
///
/// Covers two leak vectors:
/// 1. ProcessTreeCpuSampler kill-path pipe-FD leak (volume driver).
/// 2. ProcessCapture detached-child leak (descendants survive normal exit).
/// </summary>
public sealed class FdLeakTests
{
    // ── Handle-count smoke test ────────────────────────────────────────

    /// <summary>
    /// Calls <see cref="ProcessTreeCpuSampler.TrySampleTreeCpuMs"/> 50 times
    /// in a loop and asserts that the open-handle delta is &lt; 10.  Rules
    /// out a per-call FD leak on the normal path while allowing GC jitter.
    ///
    /// The kill-path leak (no <c>ReadToEnd()</c> before dispose on timeout)
    /// is a concurrency issue that accumulates across overlapping threads;
    /// this single-threaded smoke test guards against per-call regressions.
    /// </summary>
    [Fact]
    public void ProcessTreeCpuSampler_DrainedOnAllPaths()
    {
        // ps(1) required — skip gracefully when absent (sandboxed macOS).
        if (ProcessTreeCpuSampler.TrySampleTreeCpuMs(Environment.ProcessId) is null)
            return;

        // Stabilise after any prior tests in the collection.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = Process.GetCurrentProcess().HandleCount;

        const int iterations = 50;
        for (var i = 0; i < iterations; i++)
        {
            ProcessTreeCpuSampler.TrySampleTreeCpuMs(Environment.ProcessId);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var after = Process.GetCurrentProcess().HandleCount;
        var delta = after - before;

        Assert.True(delta < 10,
            $"Handle count grew by {delta} across {iterations} TrySampleTreeCpuMs calls " +
            $"(before={before}, after={after}). " +
            "Expected delta < 10; a larger delta suggests pipe FDs are not being released.");
    }

    // ── Detached-child reaping test ────────────────────────────────────

    /// <summary>
    /// Spawns a stage process via <c>ProcessCapture.RunAsync</c> that
    /// forks a detached background child (a block-forever <c>tail -f /dev/null</c>).
    /// After RunAsync returns normally, asserts that the detached child is no
    /// longer alive — the block-forever child can only die by being reaped.
    ///
    /// RED until ProcessCapture reaps the stage's process group on normal
    /// exit.  The current code only kills on timeout; detached descendants
    /// that reparent to PID 1 survive normal exit.
    /// </summary>
    [Fact]
    public async Task ProcessCapture_DetachedChildReapedAfterNormalExit()
    {
        // Process-group reaping via setpgid/kill(-pgid) is POSIX-only.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        using var repo = TestRepository.Create();

        // Script: set its own process group (so reaping works even when
        // parent-side setpgid races), fork a detached background child,
        // print its PID, exit.  The child's stdio is redirected to
        // /dev/null so it does not inherit the pipe write-ends and block
        // WaitForExitAsync on macOS.
        //
        // We use a perl script (shebang) so the process itself calls
        // setpgid(0,0) — a child perl -e would only change perl's pgid,
        // not the shell's.  Perl is guaranteed on macOS and near-universal
        // on Linux; when absent the test will fail early (non-zero exit).
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fork-detached",
            "#!/usr/bin/env perl\n" +
            "use POSIX;\n" +
            "setpgid(0,0);\n" +
            "my $pid = fork();\n" +
            "if ($pid == 0) {\n" +
            "    open(STDIN,  '<', '/dev/null');\n" +
            "    open(STDOUT, '>', '/dev/null');\n" +
            "    open(STDERR, '>', '/dev/null');\n" +
            "    exec('tail', '-f', '/dev/null');\n" +
            "}\n" +
            "print \"CHILD_PID=$pid\\n\";\n");

        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            script,
            "",
            repo.Root,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(timedOut, "Script timed out unexpectedly");
        Assert.Equal(0, exitCode);

        // Extract the child PID from captured output.
        var childPid = ParseChildPid(output);
        Assert.True(childPid.HasValue,
            $"Could not parse CHILD_PID from captured output: '{output}'");

        // Brief settle period for any reaping to take effect.
        await Task.Delay(100);

        Assert.False(IsProcessAlive(childPid.Value),
            $"Detached child PID {childPid} is still alive after ProcessCapture.RunAsync returned. " +
            "Expected the child to be reaped on normal exit.");
    }

    // ── Inherited-pipe prompt-return test ──────────────────────────────

    /// <summary>
    /// The inherited-pipe inverse of
    /// <see cref="ProcessCapture_DetachedChildReapedAfterNormalExit"/>: the forked
    /// child deliberately INHERITS the parent's stdout/stderr pipe write-ends
    /// (it does NOT redirect them to /dev/null) and then blocks forever
    /// (<c>tail -f /dev/null</c>), while the parent prints a sentinel and exits 0
    /// immediately.
    ///
    /// With async-read redirected streams, <c>WaitForExitAsync</c> does not complete
    /// until the pipes hit EOF, and EOF only arrives once every process holding the
    /// write-end closes it — so without reap-on-exit + bounded-drain the surviving
    /// child wedges RunAsync indefinitely even though the spawned process exited at
    /// once. A tightened 6 s RunAsync timeout caps that buggy hang at a fast, bounded
    /// TimedOut return rather than riding a long cap.
    ///
    /// RED until reap-on-exit + bounded-drain lands; then RunAsync returns in &lt;1 s
    /// once the parent exits, regardless of the inherited pipe-holder.
    /// </summary>
    [Fact]
    public async Task ProcessCapture_ReturnsPromptlyWhenChildInheritsPipeAndSurvives()
    {
        // Process-group reaping via setpgid/kill(-pgid) is POSIX-only.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        using var repo = TestRepository.Create();

        // Script: set its own process group (so reaping works even when the
        // parent-side setpgid races), fork a background child that INHERITS
        // stdout/stderr (no /dev/null redirect — that inherited pipe is the
        // whole point), then block forever (tail -f /dev/null).  The parent prints
        // a unique sentinel plus the child PID and falls off the end (exit 0)
        // immediately.
        //
        // Perl shebang so the process itself calls setpgid(0,0); perl is
        // guaranteed on macOS and near-universal on Linux.
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fork-inherit",
            "#!/usr/bin/env perl\n" +
            "use POSIX;\n" +
            "setpgid(0,0);\n" +
            "my $pid = fork();\n" +
            "if ($pid == 0) {\n" +
            // Intentionally do NOT touch stdio: the child inherits the
            // parent's stdout/stderr pipe write-ends and holds them open.
            "    exec('tail', '-f', '/dev/null');\n" +
            "}\n" +
            "print \"CHILD_PID=$pid\\n\";\n" +
            "print \"PARENT_DONE\\n\";\n");

        var sw = Stopwatch.StartNew();
        var (_, output, timedOut) = await ProcessCapture.RunAsync(
            script,
            "",
            repo.Root,
            // Tight 6 s cap: with a block-forever child the buggy path (no
            // reap/drain) can only fail by riding this cap to a TimedOut return,
            // so keep it short; fixed code returns in well under a second.
            TimeSpan.FromSeconds(6),
            CancellationToken.None);
        sw.Stop();

        Assert.False(timedOut,
            $"RunAsync timed out (elapsed {sw.Elapsed.TotalSeconds:F1}s); expected a prompt return on parent exit.");

        // PRIMARY discriminating assertion: RunAsync must return shortly after
        // the parent exits, not ride the surviving child's lifetime.  Buggy code
        // blocks on the inherited pipe (the child never exits) and FAILS here —
        // via this 5 s bound or the 6 s RunAsync cap above; the fix reaps +
        // bounded-drains and returns in <1 s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"RunAsync took {sw.Elapsed.TotalSeconds:F1}s to return after the parent exited; " +
            "expected < 5s. A late return means it blocked on the surviving child's inherited " +
            "stdout/stderr pipe (WaitForExitAsync waits for stream EOF) instead of ending on " +
            "process exit.");

        Assert.Contains("PARENT_DONE", output);

        // Secondary: the inherited child should also be reaped on exit. Kept
        // after the elapsed-time check so the timing assertion is the RED trigger.
        var childPid = ParseChildPid(output);
        Assert.True(childPid.HasValue,
            $"Could not parse CHILD_PID from captured output: '{output}'");
        await Task.Delay(100);
        Assert.False(IsProcessAlive(childPid.Value),
            $"Inherited child PID {childPid} is still alive after ProcessCapture.RunAsync returned. " +
            "Expected the child to be reaped on normal exit.");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static int? ParseChildPid(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("CHILD_PID=", StringComparison.Ordinal) &&
                int.TryParse(t.AsSpan("CHILD_PID=".Length), out var pid))
                return pid;
        }

        return null;
    }

    /// <summary>POSIX existence check via kill -0 (null signal).</summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo("/bin/kill", $"-0 {pid}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            proc.Start();
            proc.WaitForExit(1_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            // If kill itself cannot start, assume the process doesn't exist.
            return false;
        }
    }
}
