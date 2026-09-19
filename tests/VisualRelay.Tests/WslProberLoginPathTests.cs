using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The PATH the distro user's own login shell builds. Measured on WSL 2.7.14 with
/// Ubuntu 26.04: rustup adds ~/.cargo/bin through ~/.profile and nvm adds node only
/// through the interactive part of ~/.bashrc, so a plain <c>wsl --exec</c> finds
/// neither. The probe reads that PATH once so every launch can carry it.
/// </summary>
public sealed class WslProberLoginPathTests
{
    [Fact]
    public async Task LoginShellPath_IsReadBetweenTheMarkers_IgnoringWhatTheRcFilesPrint()
    {
        var wsl = ScriptedWsl.HealthyUbuntu();

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, ScriptedWsl.Exe, wsl.ReadVmPlatform, CancellationToken.None);

        Assert.Equal(ScriptedWsl.UserPath, probe.UserPath);
    }

    [Theory]
    [InlineData(1, "sh: 1: exec: /usr/bin/fish: not found\n")]
    [InlineData(0, "no markers at all\n")]
    [InlineData(-1, "")]
    public async Task LoginShellPathUnreadable_LeavesItNull_AndTheProbeStaysUsable(int exitCode, string output)
    {
        var wsl = ScriptedWsl.HealthyUbuntu().On($"-d Ubuntu --exec sh -c {WslProber.LoginPathScript}", exitCode, output);

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, ScriptedWsl.Exe, wsl.ReadVmPlatform, CancellationToken.None);

        Assert.Null(probe.UserPath);
        Assert.True(probe.IsUsable, probe.Diagnostics);
        Assert.Contains("login shell PATH", probe.Diagnostics);
    }

    [Fact]
    public async Task UnusableDistro_DoesNotReadTheLoginShellPath()
    {
        var wsl = ScriptedWsl.HealthyUbuntu().On("-d Ubuntu --exec sh -lc command -v nono", 1, "");

        await WslProber.ProbeAsync(wsl.RunAsync, null, ScriptedWsl.Exe, wsl.ReadVmPlatform, CancellationToken.None);

        Assert.DoesNotContain(wsl.Calls, argv => argv.Contains(WslProber.LoginPathScript));
    }

    [Fact]
    public void Context_CarriesTheUserPathFromAUsableProbe()
    {
        var context = WslContextResolver.FromProbe(WslProbeFixtures.Usable() with { UserPath = ScriptedWsl.UserPath });

        Assert.Equal(ScriptedWsl.UserPath, context!.UserPath);
    }
}
