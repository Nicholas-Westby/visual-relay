using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The whole <c>setup-wsl</c> run: probe, plan, say what will happen, do it, then probe
/// again. Success is the gate's verdict on that second probe, never the steps' exit codes.
/// </summary>
public sealed class WslSetupTests
{
    private const string User = "alice";

    private readonly List<string> _log = [];

    [Fact]
    public async Task WhatSetupCannotFix_GetsTheGatesMessage_AndNothingRuns()
    {
        var exitCode = await RunAsync([WslProbeFixtures.Wsl1()]);

        Assert.Equal(127, exitCode);
        Assert.Equal([WslGate.Decide(WslProbeFixtures.Wsl1()).Message!], _log);
    }

    [Fact]
    public async Task AReadyMachine_IsLeftAlone()
    {
        var exitCode = await RunAsync([WslProbeFixtures.Usable()]);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(_log, line => line.StartsWith("wsl.exe", StringComparison.Ordinal));
        Assert.Contains("nothing to set up", Assert.Single(_log));
    }

    [Fact]
    public async Task ThePlan_IsShownBeforeAnythingRuns()
    {
        await RunAsync([WslProbeFixtures.NoDistro(), WslProbeFixtures.Usable()]);

        var plan = WslSetupPlan.For(WslProbeFixtures.NoDistro(), User);
        var shown = _log.FindIndex(line => plan.Steps.All(step => line.Contains(step.Description)));
        Assert.True(shown >= 0, "the plan was never shown:\n" + string.Join('\n', _log));
        Assert.True(shown < _log.FindIndex(line => line.StartsWith("wsl.exe", StringComparison.Ordinal)));
        Assert.Contains("None of this needs administrator rights", _log[shown]);
    }

    /// <summary>
    /// WSL's first install needs a restart before WSL can run a distro, so setup stops there,
    /// says how to finish, and plans nothing that could only be guessed at before WSL answers:
    /// the one probe given here would run out if it tried.
    /// </summary>
    [Fact]
    public async Task InstallingWsl_StopsAtTheRestartThatFinishesIt()
    {
        var exitCode = await RunAsync([WslProbeFixtures.InboxStubOnly()]);

        Assert.Equal(3, exitCode);
        Assert.Contains("approve", _log[0]);
        Assert.Equal(["wsl.exe as administrator --install --no-distribution"],
            _log.Where(line => line.StartsWith("wsl.exe", StringComparison.Ordinal)));
        Assert.Contains("Restart Windows", _log[^1]);
        Assert.Contains("setup-wsl", _log[^1]);
    }

    /// <summary>
    /// The state the user's first run left on 2026-09-19: the platform turned on, Windows not yet
    /// restarted. Nothing setup can run would help, so it runs nothing and asks for the restart.
    /// </summary>
    [Fact]
    public async Task APendingRestart_RunsNothing_AndAsksForTheRestart()
    {
        var exitCode = await RunAsync([WslProbeFixtures.VmPlatformAwaitingRestart()]);

        Assert.Equal(127, exitCode);
        Assert.Contains("Restart Windows", Assert.Single(_log));
    }

    /// <summary>A step can exit 0 and still leave the sandbox unusable, as a kernel without Landlock does.</summary>
    [Fact]
    public async Task Ready_IsClaimedOnlyWhenAFreshProbeSaysSo()
    {
        var exitCode = await RunAsync([WslProbeFixtures.NonoMissing(), WslProbeFixtures.LandlockInactive()]);

        Assert.Equal(127, exitCode);
        Assert.Equal(WslGate.Decide(WslProbeFixtures.LandlockInactive()).Message, _log[^1]);
        Assert.DoesNotContain(_log, line => line.Contains("WSL is ready"));
    }

    [Fact]
    public async Task ASetupThatWorked_SaysSo_AndTellsANewUserHowToUseSudo()
    {
        var exitCode = await RunAsync([WslProbeFixtures.NoDistro(), WslProbeFixtures.Usable()]);

        Assert.Equal(0, exitCode);
        Assert.Contains("WSL is ready", _log[^1]);
        Assert.Contains("wsl -d Ubuntu -u root passwd alice", _log[^1]);
    }

    [Fact]
    public async Task ASetupThatCreatedNoUser_SaysNothingAboutPasswords()
    {
        await RunAsync([WslProbeFixtures.NonoMissing(), WslProbeFixtures.Usable()]);

        Assert.Contains("WSL is ready", _log[^1]);
        Assert.DoesNotContain("passwd", _log[^1]);
    }

    [Fact]
    public async Task AFailedStep_EndsTheRun()
    {
        var exitCode = await RunAsync([WslProbeFixtures.NonoMissing()], failing: WslSetupScripts.Nono);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("visual-relay: WSL setup stopped at step 2 of 2", _log[^1]);
    }

    /// <summary>Runs setup against <paramref name="probes"/> in turn and a wsl.exe that logs each call.</summary>
    private Task<int> RunAsync(WslProbe[] probes, string? failing = null)
    {
        var next = new Queue<WslProbe>(probes);
        var host = new WslSetupHost(
            _ => Task.FromResult(next.Dequeue()),
            (argv, _) =>
            {
                _log.Add("wsl.exe " + string.Join(' ', argv));
                if (argv is ["-l", "-v"])
                    return Task.FromResult((0, "  NAME      STATE           VERSION\n* Ubuntu    Stopped         2\n"));
                return Task.FromResult(failing is not null && argv.Contains(failing) ? (1, "failed\n") : (0, ""));
            },
            (argv, _) =>
            {
                _log.Add("wsl.exe as administrator " + string.Join(' ', argv));
                return Task.FromResult((0, ""));
            },
            User, LocalNonoDeb: null);
        return WslSetup.RunAsync(host, _log.Add, CancellationToken.None);
    }
}
