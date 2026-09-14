using System.Diagnostics;
using System.Runtime.InteropServices;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class FdLeakTests
{
    // ── Stopped-command reaping test ───────────────────────────────────

    /// <summary>
    /// A command stopped at its time limit must not leave the processes it started running. Measured
    /// on the Mac: bootstrap's 60 s check of <c>cargo test</c> timed out, cargo quit on the interrupt,
    /// and a rustc it had started was still compiling after bootstrap had moved on. VR's setpgid on
    /// the child loses the race with exec, so the interrupt meant for the whole group reached only the
    /// root, and a root that exits within the grace window skipped the hard kill. The child here ignores
    /// the interrupt, like a process that never receives one, so only the kill can end it.
    /// </summary>
    [Fact]
    public async Task ProcessCapture_TimedOutRootThatQuitsOnInterrupt_LeavesNoChildRunning()
    {
        // Genuine OS-semantics: real signals, a real ps table and the real grace window. Opt-in only.
        SlowIntegration.SkipIfNotOptedIn();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        using var repo = TestRepository.Create();

        // No setpgid of its own, unlike the scripts above: the child stays wherever the launch left it.
        var script = await StageTestHelpers.WriteExecutableAsync(
            repo.Root,
            "quits-on-interrupt",
            "#!/usr/bin/env perl\n" +
            "my $pid = fork();\n" +
            "if ($pid == 0) {\n" +
            "    $SIG{INT} = 'IGNORE';\n" +
            "    open(STDIN,  '<', '/dev/null');\n" +
            "    open(STDOUT, '>', '/dev/null');\n" +
            "    open(STDERR, '>', '/dev/null');\n" +
            "    exec('tail', '-f', '/dev/null');\n" +
            "}\n" +
            "$| = 1;\n" +
            "print \"CHILD_PID=$pid\\n\";\n" +
            "$SIG{INT} = sub { exit 130 };\n" +
            "waitpid($pid, 0);\n");

        var (_, output, timedOut) = await ProcessCapture.RunAsync(
            script,
            "",
            repo.Root,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(timedOut, "The script should have been stopped at its time limit");
        var childPid = ParseChildPid(output);
        Assert.True(childPid.HasValue, $"Could not parse CHILD_PID from captured output: '{output}'");

        await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System);

        Assert.False(IsProcessAlive(childPid.Value),
            $"Child PID {childPid} is still running after its timed-out parent was stopped.");
    }

    /// <summary>
    /// The children get the interrupt too, as they would have from a working process group: a child
    /// that stops on it (a compiler does) is gone at once, so the stop does not sit out the 10 s grace
    /// window before its kill.
    /// </summary>
    [Fact]
    public async Task ProcessCapture_TimedOutRootThatQuitsOnInterrupt_InterruptsItsChildrenToo()
    {
        // Genuine OS-semantics: real signals over a real grace window. Opt-in only.
        SlowIntegration.SkipIfNotOptedIn();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        using var repo = TestRepository.Create();
        var script = await StageTestHelpers.WriteExecutableAsync(
            repo.Root,
            "quits-with-its-child",
            "#!/usr/bin/env perl\n" +
            "my $pid = fork();\n" +
            "if ($pid == 0) {\n" +
            "    open(STDIN,  '<', '/dev/null');\n" +
            "    open(STDOUT, '>', '/dev/null');\n" +
            "    open(STDERR, '>', '/dev/null');\n" +
            "    exec('tail', '-f', '/dev/null');\n" +
            "}\n" +
            "$| = 1;\n" +
            "print \"CHILD_PID=$pid\\n\";\n" +
            "$SIG{INT} = sub { exit 130 };\n" +
            "waitpid($pid, 0);\n");

        var sw = Stopwatch.StartNew();
        var (_, output, timedOut) = await ProcessCapture.RunAsync(
            script,
            "",
            repo.Root,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        sw.Stop();

        Assert.True(timedOut, "The script should have been stopped at its time limit");
        var childPid = ParseChildPid(output);
        Assert.True(childPid.HasValue, $"Could not parse CHILD_PID from captured output: '{output}'");
        Assert.False(IsProcessAlive(childPid.Value), $"Child PID {childPid} is still running after the stop.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7),
            $"The stop waited {sw.Elapsed.TotalSeconds:F1}s: the child was not interrupted and sat out the grace window.");
    }
}
