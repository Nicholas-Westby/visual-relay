using VisualRelay.Cli.Gates;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The Windows launch gate, decided purely from a <see cref="WslProbe"/>. A usable
/// probe proceeds; every other probe exits 127 with a message that names the FIRST
/// failing check, its exact fix, and always the toolchain consequence (commands run
/// inside the distro; a Windows-only toolchain is not supported).
/// </summary>
public sealed class WslGateDecisionTests
{
    private const string Consequence =
        "install the toolchain there. A Windows-only toolchain (MSBuild against .NET Framework, " +
        "Visual Studio build tools, Unity on Windows, anything that needs an .exe) is not supported.";

    private static WslProbe Fixture(string name) => name switch
    {
        nameof(WslProbeFixtures.NoWsl) => WslProbeFixtures.NoWsl(),
        nameof(WslProbeFixtures.NoDistro) => WslProbeFixtures.NoDistro(),
        nameof(WslProbeFixtures.RequestedDistroMissing) => WslProbeFixtures.RequestedDistroMissing(),
        nameof(WslProbeFixtures.Wsl1) => WslProbeFixtures.Wsl1(),
        nameof(WslProbeFixtures.NonoMissing) => WslProbeFixtures.NonoMissing(),
        nameof(WslProbeFixtures.LandlockInactive) => WslProbeFixtures.LandlockInactive(),
        nameof(WslProbeFixtures.HomeUnknown) => WslProbeFixtures.HomeUnknown(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    [Fact]
    public void Usable_Proceeds()
    {
        Assert.Equal((0, (string?)null), WslGate.Decide(WslProbeFixtures.Usable()));
    }

    [Theory]
    [InlineData(nameof(WslProbeFixtures.NoWsl))]
    [InlineData(nameof(WslProbeFixtures.NoDistro))]
    [InlineData(nameof(WslProbeFixtures.RequestedDistroMissing))]
    [InlineData(nameof(WslProbeFixtures.Wsl1))]
    [InlineData(nameof(WslProbeFixtures.NonoMissing))]
    [InlineData(nameof(WslProbeFixtures.LandlockInactive))]
    [InlineData(nameof(WslProbeFixtures.HomeUnknown))]
    public void EveryFailure_Exits127AndEndsWithTheToolchainConsequence(string fixture)
    {
        var (exitCode, message) = WslGate.Decide(Fixture(fixture));

        Assert.Equal(127, exitCode);
        Assert.NotNull(message);
        Assert.StartsWith("visual-relay: ", message);
        Assert.Contains("Visual Relay on Windows runs your project's build and test commands inside the WSL2 distro ", message);
        Assert.EndsWith(Consequence, message.TrimEnd());
    }

    [Fact]
    public void WslMissing_NamesTheInstallAndTheRebootAndSaysTheDistroIsTheOneYouInstall()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.NoWsl());

        Assert.Contains("wsl.exe", message);
        Assert.Contains("wsl --install -d Ubuntu", message);
        Assert.Contains("elevated PowerShell", message);
        Assert.Contains("reboot", message);
        Assert.Contains("wsl -d Ubuntu", message);
        Assert.Contains("inside the WSL2 distro you install;", message);
    }

    [Fact]
    public void NoDistro_NamesTheInstallAndTheSelector()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.NoDistro());

        Assert.Contains("no WSL distro", message);
        Assert.Contains("wsl --install -d Ubuntu", message);
        Assert.Contains("VR_WSL_DISTRO", message);
    }

    [Fact]
    public void RequestedDistroMissing_NamesItAndTheInstalledOnes()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.RequestedDistroMissing());

        Assert.Contains("'Fedora'", message);
        Assert.Contains("VR_WSL_DISTRO", message);
        Assert.Contains("Ubuntu, Debian", message);
        Assert.Contains("wsl --install -d Fedora", message);
    }

    [Fact]
    public void Wsl1Distro_NamesTheConversion()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.Wsl1());

        Assert.Contains("'Debian' is a WSL1 distro", message);
        Assert.Contains("4.4.0-19041-Microsoft", message);
        Assert.Contains("wsl --set-version Debian 2", message);
        Assert.Contains("VR_WSL_DISTRO", message);
    }

    [Fact]
    public void NonoMissing_NamesTheInstallInsideTheDistro()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.NonoMissing());

        Assert.Contains("nono was not found inside the WSL distro 'Ubuntu'", message);
        Assert.Contains("nono 0.75.0", message);
        Assert.Contains("curl -fsSL https://nono.sh/install.sh | sh", message);
        Assert.Contains(".deb", message);
        Assert.Contains("v0.75.0", message);
        Assert.Contains("login shell", message);
        Assert.Contains("inside the WSL2 distro 'Ubuntu';", message);
    }

    [Fact]
    public void LandlockInactive_NamesTheKernelFix()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.LandlockInactive());

        Assert.Contains("Landlock is not active", message);
        Assert.Contains(@"%UserProfile%\.wslconfig", message);
        Assert.Contains("kernel=", message);
        Assert.Contains("kernelCommandLine=", message);
        Assert.Contains("wsl --update", message);
        Assert.Contains("wsl --shutdown", message);
        Assert.Contains("5.15.57.1", message);
        Assert.Contains("lockdown,yama,bpf", message);
    }

    [Fact]
    public void HomeUnknown_NamesTheFirstRunSetup()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.HomeUnknown());

        Assert.Contains("home directory", message);
        Assert.Contains("wsl -d Ubuntu", message);
    }

    [Fact]
    public void SeveralChecksFailing_TheFirstOneIsNamed()
    {
        var probe = WslProbeFixtures.NonoMissing() with { LandlockActive = false, DistroHome = null };

        var (_, message) = WslGate.Decide(probe);

        Assert.Contains("nono was not found", message);
        Assert.DoesNotContain(".wslconfig", message);
        Assert.DoesNotContain("home directory", message);
    }
}
