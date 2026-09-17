using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The probe's eighth step: git inside the distro. Every workspace git call on
/// Windows runs there, and until this step existed nothing asked whether git was
/// installed — the README telling a new user to apt-install it was the only
/// protection. Split from <see cref="WslProberTests"/> to keep that file within the
/// size guard.
/// </summary>
public sealed class WslProberGitTests
{
    private const string Exe = ScriptedWsl.Exe;

    private static ScriptedWsl HealthyUbuntu() => ScriptedWsl.HealthyUbuntu();

    [Fact]
    public async Task GitMissing_LeavesGitNullAndTheProbeUnusable()
    {
        var wsl = HealthyUbuntu()
            .On($"-d Ubuntu --exec env PATH={ScriptedWsl.UserPath} sh -c command -v git", 1, string.Empty);

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        Assert.Null(probe.GitPath);
        Assert.False(probe.IsUsable);
        Assert.True(probe.LaunchPrerequisitesMet, "every sandbox prerequisite still holds");
        Assert.Contains("command -v git", probe.Diagnostics!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Against the login shell's PATH, because that is the PATH the routed git
    /// launch gets: git installed only where an rc file puts it must still count.
    /// </summary>
    [Fact]
    public async Task GitStep_RunsWithTheLoginShellsPath()
    {
        var wsl = HealthyUbuntu();

        await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        var gitCall = wsl.Calls.Last();
        Assert.Contains("env", gitCall);
        Assert.Contains($"PATH={ScriptedWsl.UserPath}", gitCall);
    }

    /// <summary>With no readable login PATH the launch keeps the distro default, so the check does too.</summary>
    [Fact]
    public async Task GitStep_WithNoLoginPath_AsksWithoutTheEnvPrefix()
    {
        var wsl = HealthyUbuntu()
            .On($"-d Ubuntu --exec sh -c {WslProber.LoginPathScript}", 1, "no path here")
            .On("-d Ubuntu --exec sh -c command -v git", 0, "/usr/bin/git\n");

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        Assert.Null(probe.UserPath);
        Assert.Equal("/usr/bin/git", probe.GitPath);
        Assert.True(probe.IsUsable);
    }
}
