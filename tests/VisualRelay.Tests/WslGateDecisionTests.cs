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
        nameof(WslProbeFixtures.InboxStubOnly) => WslProbeFixtures.InboxStubOnly(),
        nameof(WslProbeFixtures.NoDistro) => WslProbeFixtures.NoDistro(),
        nameof(WslProbeFixtures.RequestedDistroMissing) => WslProbeFixtures.RequestedDistroMissing(),
        nameof(WslProbeFixtures.Wsl1) => WslProbeFixtures.Wsl1(),
        nameof(WslProbeFixtures.NonoMissing) => WslProbeFixtures.NonoMissing(),
        nameof(WslProbeFixtures.LandlockInactive) => WslProbeFixtures.LandlockInactive(),
        nameof(WslProbeFixtures.HomeUnknown) => WslProbeFixtures.HomeUnknown(),
        nameof(WslProbeFixtures.GitMissing) => WslProbeFixtures.GitMissing(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    [Fact]
    public void Usable_Proceeds()
    {
        Assert.Equal((0, (string?)null), WslGate.Decide(WslProbeFixtures.Usable()));
    }

    [Theory]
    [InlineData(nameof(WslProbeFixtures.NoWsl))]
    [InlineData(nameof(WslProbeFixtures.InboxStubOnly))]
    [InlineData(nameof(WslProbeFixtures.NoDistro))]
    [InlineData(nameof(WslProbeFixtures.RequestedDistroMissing))]
    [InlineData(nameof(WslProbeFixtures.Wsl1))]
    [InlineData(nameof(WslProbeFixtures.NonoMissing))]
    [InlineData(nameof(WslProbeFixtures.LandlockInactive))]
    [InlineData(nameof(WslProbeFixtures.HomeUnknown))]
    [InlineData(nameof(WslProbeFixtures.GitMissing))]
    public void EveryFailure_Exits127AndEndsWithTheToolchainConsequence(string fixture)
    {
        var (exitCode, message) = WslGate.Decide(Fixture(fixture));

        Assert.Equal(127, exitCode);
        Assert.NotNull(message);
        Assert.StartsWith("visual-relay: ", message);
        Assert.Contains("Visual Relay on Windows runs your project's build and test commands inside the WSL2 distro ", message);
        Assert.EndsWith(Consequence, message.TrimEnd());
    }

    /// <summary>
    /// What <c>setup-wsl</c> can finish, the gate offers before the manual fix. It never offers
    /// it for what setup leaves to the user: a Windows without even the inbox wsl.exe, a WSL1
    /// distro, Landlock, or a distro whose first run never finished.
    /// </summary>
    [Theory]
    [InlineData(nameof(WslProbeFixtures.NoDistro), true)]
    [InlineData(nameof(WslProbeFixtures.RequestedDistroMissing), true)]
    [InlineData(nameof(WslProbeFixtures.NonoMissing), true)]
    [InlineData(nameof(WslProbeFixtures.GitMissing), true)]
    [InlineData(nameof(WslProbeFixtures.InboxStubOnly), true)]
    [InlineData(nameof(WslProbeFixtures.NoWsl), false)]
    [InlineData(nameof(WslProbeFixtures.Wsl1), false)]
    [InlineData(nameof(WslProbeFixtures.LandlockInactive), false)]
    [InlineData(nameof(WslProbeFixtures.HomeUnknown), false)]
    public void SetupWsl_IsOfferedFirst_ExactlyWhereItCanFinishTheJob(string fixture, bool offered)
    {
        var (_, message) = WslGate.Decide(Fixture(fixture));

        var offer = message!.IndexOf(@"run `.\visual-relay.cmd setup-wsl`", StringComparison.Ordinal);
        Assert.Equal(offered, offer >= 0);
        if (offered)
            Assert.True(offer < message.IndexOf("\n\n", message.IndexOf("\n\n", StringComparison.Ordinal) + 2, StringComparison.Ordinal),
                "the offer comes before the manual fix:\n" + message);
    }

    [Fact]
    public void InstallingWslItself_IsOffered_WithTheApprovalItNeeds()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.InboxStubOnly());

        Assert.Contains(@"run `.\visual-relay.cmd setup-wsl`", message);
        Assert.Contains("approve", message);
        Assert.DoesNotContain("no administrator rights", message);
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
    public void InboxStubOnly_SaysWslIsNotInstalled_WithTheElevatedInstallAndTheReboot()
    {
        // Windows 11 ships wsl.exe before WSL is installed, so "found" is not "installed".
        var (_, message) = WslGate.Decide(WslProbeFixtures.InboxStubOnly());

        Assert.StartsWith("visual-relay: WSL is not installed.", message);
        Assert.Contains("wsl --install -d Ubuntu", message);
        Assert.Contains("elevated PowerShell", message);
        Assert.Contains("reboot", message);
        // Measured on Windows 11 25H2: the first install brought WSL but no distro.
        Assert.Contains("run `wsl --install -d Ubuntu` again", message);
        Assert.DoesNotContain("no WSL distro", message);
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
        // The installer takes the latest release unless told otherwise; VR pins 0.75.0.
        Assert.Contains("curl -fsSL https://nono.sh/install.sh | NONO_VERSION=v0.75.0 sh", message);
        Assert.Contains("--no-install-recommends", message);
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
        Assert.Contains("No supported Landlock ABI detected", message);
    }

    [Fact]
    public void HomeUnknown_NamesTheFirstRunSetup()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.HomeUnknown());

        Assert.Contains("home directory", message);
        Assert.Contains("wsl -d Ubuntu", message);
    }

    /// <summary>
    /// Every workspace git call on Windows runs inside the distro, so a distro
    /// without git is refused like a missing sandbox, and the fix is the one command
    /// that installs it THERE, not on Windows.
    /// </summary>
    [Fact]
    public void GitMissing_NamesTheInstallInsideTheDistro()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.GitMissing());

        Assert.Contains("git was not found inside the WSL distro 'Ubuntu'", message);
        Assert.Contains("sudo apt install -y git", message);
        Assert.Contains("wsl -d Ubuntu --exec sh -lc 'command -v git'", message);
    }

    [Fact]
    public void SeveralChecksFailing_TheFirstOneIsNamed()
    {
        var probe = WslProbeFixtures.NonoMissing() with { LandlockActive = false, DistroHome = null, GitPath = null };

        var (_, message) = WslGate.Decide(probe);

        Assert.Contains("nono was not found", message);
        Assert.DoesNotContain(".wslconfig", message);
        Assert.DoesNotContain("home directory", message);
        Assert.DoesNotContain("git was not found", message);
    }

    /// <summary>Git is the LAST check, so a missing home is still named ahead of it.</summary>
    [Fact]
    public void HomeAndGitBothMissing_TheHomeIsNamed()
    {
        var (_, message) = WslGate.Decide(WslProbeFixtures.HomeUnknown() with { GitPath = null });

        Assert.Contains("home directory", message);
        Assert.DoesNotContain("git was not found", message);
    }
}
