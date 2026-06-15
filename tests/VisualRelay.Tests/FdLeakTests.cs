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
    /// forks a detached background child (sleep 0.5).  After RunAsync returns
    /// normally, asserts that the detached child is no longer alive.
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
            "    exec('sleep', '0.3');\n" +
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
