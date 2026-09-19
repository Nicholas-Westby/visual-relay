using System.Globalization;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Windows runtime probe: the watchdog stops a deliberately hung command and the
/// LINUX process is gone. Killing wsl.exe never signals the distro's child (the
/// relay just exits, orphaning the tree), which is why the envelope puts the
/// command in its own session with <c>setsid</c> and why
/// <see cref="WslProcessTreeControl"/> signals the process group from INSIDE the
/// distro. Only a real run proves that chain: the pid file is written, the pid is
/// its own group leader, one TERM to the group ends it, and wsl.exe returns.
/// <para>The hung child blocks on <c>tail -f /dev/null</c> rather than a sleep of N
/// seconds: the suite's real-sleep guard rejects a sleep literal in test sources and
/// names a block-forever child as the substitute. The proof is identical — a command
/// that never exits on its own — and nothing here waits on the wall clock.</para>
/// </summary>
public sealed class WslWatchdogKillsHungTreeTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Announces itself on stdout, then becomes the hung command in place. The echo
    /// is the signal that the envelope has already forked and recorded the pid (it
    /// writes the pid file between the fork and the wait), so the test never polls.
    /// </summary>
    private const string HangScript = "echo vr-probe-started; exec tail -f /dev/null";

    [Fact]
    public async Task HungTree_StoppedThroughTheTreeControl_LeavesNoLinuxProcess()
    {
        var wsl = SkipIfNotOptedIn();
        await RunAsync(WslLauncher.BuildPlain(
            wsl.WslExePath, wsl.Distro, ["mkdir", "-p", WslProcessControl.PidFileDirectory]));
        var pidFile = WslProcessControl.PidFilePath("vr-kill-" + Guid.NewGuid().ToString("N")[..8], "probe");

        var launch = WslLauncher.Build(
            wsl.WslExePath, wsl.Distro, wsl.DistroHome, pidFile,
            nonoPrefix: [], program: "/bin/sh", args: ["-c", HangScript]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ProcessCapture.RunAsync(
            launch.FileName, launch.Arguments, Path.GetTempPath(), StepTimeout, CancellationToken.None,
            environment: launch.Environment,
            onActivity: signal => { if (signal == "stdout") started.TrySetResult(); });

        // The echo proves the envelope forked and recorded the pid; if wsl.exe returns
        // first the launch never got that far, and its output says why.
        if (await Task.WhenAny(started.Task, run) != started.Task)
        {
            var (earlyExit, earlyOutput, _) = await run;
            Assert.Fail($"the hung child never started: wsl.exe exited {earlyExit}\n{earlyOutput}");
        }

        var pid = await ReadRecordedPidAsync(wsl, pidFile);

        // Before the stop: the recorded pid is alive AND leads its own process group,
        // which is what makes one signal to -pgid reach the whole tree.
        var (aliveExit, groupId) = await RunAsync(WslLauncher.BuildPlain(
            wsl.WslExePath, wsl.Distro, ["ps", "-o", "pgid=", "-p", pid]));
        Assert.Equal(0, aliveExit);
        Assert.Equal(pid, groupId.Trim());

        var control = new WslProcessTreeControl(wsl, pidFile, RunCapturedAsync);
        await control.StopAsync(graceful: true, CancellationToken.None);

        // wsl.exe returns because the envelope's `wait` returned: the signal crossed
        // the boundary rather than being swallowed by the Windows-side process.
        var (exitCode, output, timedOut) = await run;
        Assert.False(timedOut, $"the hung tree outlived a TERM to its process group: {output}");
        Assert.NotEqual(0, exitCode);

        // The single proof call: the Linux process is gone, not merely detached.
        var (psExit, psOutput) = await RunAsync(WslLauncher.BuildPlain(
            wsl.WslExePath, wsl.Distro, ["ps", "-p", pid]));
        Assert.True(psExit != 0, $"pid {pid} is still running inside the distro: {psOutput}");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// The probe gate: the platform, the opt-in marker the sandbox-skip guards
    /// recognise by name, and a usable WSL2 distro with nono (what the WSL gate
    /// checks before a launch). Bare-named like <c>NonoRealBuildTests</c>'s helper so
    /// every call site carries the recognised opt-out token.
    /// </summary>
    private static WslContext SkipIfNotOptedIn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the WSL sandbox is the Windows arm");
        NonoIntegration.SkipIfNotOptedIn("VR_RUN_NONO_INTEGRATION=1 required for the real WSL probes.");
        var wsl = NonoIntegration.ThisMachinesWsl();
        Assert.SkipUnless(wsl is not null, "no usable WSL2 distro with nono on this host");
        return wsl!;
    }

    /// <summary>The pid the envelope recorded, as the distro prints it.</summary>
    private static async Task<string> ReadRecordedPidAsync(WslContext wsl, string pidFile)
    {
        var (exitCode, output) = await RunAsync(WslLauncher.BuildPlain(
            wsl.WslExePath, wsl.Distro, WslProcessControl.ReadPidFileArgv(pidFile)));
        Assert.True(exitCode == 0, $"the envelope wrote no pid file at {pidFile}: {output}");
        var pid = output.Trim();
        Assert.True(int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0,
            $"the pid file held '{pid}' instead of a pid");
        return pid;
    }

    private static Task<(int ExitCode, string Output)> RunCapturedAsync(WslLaunch launch, CancellationToken ct) =>
        RunAsync(launch, ct);

    private static async Task<(int ExitCode, string Output)> RunAsync(
        WslLaunch launch, CancellationToken ct = default)
    {
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            launch.FileName, launch.Arguments, Path.GetTempPath(), StepTimeout, ct,
            environment: launch.Environment);
        Assert.False(timedOut, $"wsl.exe did not finish within {StepTimeout.TotalSeconds:F0}s: {output}");
        return (exitCode, output);
    }
}
