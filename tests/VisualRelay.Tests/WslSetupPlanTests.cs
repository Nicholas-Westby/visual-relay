using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// What <c>setup-wsl</c> would do for a machine, decided from the probe alone. Everything a
/// distro needs can be done without Windows administrator rights once WSL itself is present
/// (measured on Windows 11, 2026-09-19: a new distro, a user, git and nono in about 130 s,
/// no UAC prompt, no reboot, no questions). WSL itself is installed through the Windows
/// administrator prompt and then needs a restart; Landlock and a WSL1 distro stay manual.
/// </summary>
public sealed class WslSetupPlanTests
{
    private const string User = "alice";

    [Fact]
    public void AMachineWithNoDistro_GetsUbuntuAUserAndTheTools()
    {
        var plan = WslSetupPlan.For(WslProbeFixtures.NoDistro(), User);

        Assert.Null(plan.Blocker);
        Assert.Collection(plan.Steps,
            step => Assert.Equal(new InstallDistroStep("Ubuntu", "Ubuntu"), step),
            step => Assert.Equal(new CreateUserStep("Ubuntu", User), step),
            step => Assert.Equal(new InstallPackagesStep("Ubuntu"), step),
            step => Assert.Equal(new InstallNonoStep("Ubuntu"), step));
    }

    /// <summary>
    /// A named distro that does not exist yet is installed under that name, next to any other:
    /// Ubuntu, unless the name is itself an apt-based distro WSL can install, which then is it.
    /// </summary>
    [Theory]
    [InlineData("VrNoSuchDistro", "Ubuntu")]
    [InlineData("Ubuntu-24.04", "Ubuntu-24.04")]
    [InlineData("Debian", "Debian")]
    public void ANamedDistroThatIsMissing_IsInstalledUnderThatName(string requested, string image)
    {
        var probe = WslProbeFixtures.RequestedDistroMissing() with { RequestedDistro = requested };

        var plan = WslSetupPlan.For(probe, User);

        Assert.Null(plan.Blocker);
        Assert.Equal(new InstallDistroStep(image, requested), plan.Steps[0]);
        Assert.Equal(new CreateUserStep(requested, User), plan.Steps[1]);
    }

    /// <summary>
    /// An existing distro keeps its own user: only a distro this setup installs gets one, so a
    /// distro that already runs as someone is never given a second default.
    /// </summary>
    [Fact]
    public void AnExistingDistroWithoutNono_GetsThePackagesAndNonoOnly()
    {
        var plan = WslSetupPlan.For(WslProbeFixtures.NonoMissing(), User);

        Assert.Null(plan.Blocker);
        Assert.Equal([new InstallPackagesStep("Ubuntu"), new InstallNonoStep("Ubuntu")], plan.Steps);
    }

    [Fact]
    public void AnExistingDistroWithoutGit_GetsThePackagesOnly()
    {
        var plan = WslSetupPlan.For(WslProbeFixtures.GitMissing(), User);

        Assert.Equal([new InstallPackagesStep("Ubuntu")], plan.Steps);
    }

    [Fact]
    public void AUsableDistro_NeedsNothing()
    {
        var plan = WslSetupPlan.For(WslProbeFixtures.Usable(), User);

        Assert.Empty(plan.Steps);
        Assert.Null(plan.Blocker);
    }

    /// <summary>
    /// A Windows whose wsl.exe is only the inbox stub gets WSL installed and nothing else yet:
    /// the distro can only be planned once WSL answers, which is after the restart.
    /// </summary>
    [Fact]
    public void AWindowsWithoutWsl_GetsWslItselfInstalled_AndNothingElseYet()
    {
        var plan = WslSetupPlan.For(WslProbeFixtures.InboxStubOnly(), User);

        Assert.Null(plan.Blocker);
        var step = Assert.Single(plan.Steps);
        Assert.IsType<InstallWslStep>(step);
    }

    [Fact]
    public void OnlyInstallingWslItself_NeedsAdministratorApproval()
    {
        var steps = WslSetupPlan.For(WslProbeFixtures.InboxStubOnly(), User).Steps
            .Concat(WslSetupPlan.For(WslProbeFixtures.NoDistro(), User).Steps);

        Assert.All(steps, step => Assert.Equal(step is InstallWslStep, step.NeedsAdministrator));
    }

    /// <summary>What needs a kernel, an older Windows or the user's own decision is left to them.</summary>
    [Theory]
    [MemberData(nameof(Blocked))]
    public void WhatSetupCannotDo_IsLeftToTheUserWithTheGatesOwnFix(string name)
    {
        var probe = BlockedProbes[name];

        var plan = WslSetupPlan.For(probe, User);

        Assert.Empty(plan.Steps);
        Assert.Equal(WslGate.Decide(probe).Message, plan.Blocker);
    }

    public static TheoryData<string> Blocked => new(BlockedProbes.Keys);

    private static readonly Dictionary<string, WslProbe> BlockedProbes = new()
    {
        ["no wsl.exe"] = WslProbeFixtures.NoWsl(),
        ["wsl1"] = WslProbeFixtures.Wsl1(),
        ["landlock inactive"] = WslProbeFixtures.LandlockInactive(),
        ["home unknown"] = WslProbeFixtures.HomeUnknown(),
    };

    [Theory]
    [InlineData("Alice", "alice")]
    [InlineData("J.Smith", "jsmith")]
    [InlineData("_svc", "_svc")]
    [InlineData("9lives", "vr")]
    [InlineData("", "vr")]
    [InlineData("Émile", "emile")]
    public void TheLinuxUser_IsTheWindowsUserMadeValid(string windowsUser, string expected)
    {
        Assert.Equal(expected, WslSetupPlan.LinuxUserFor(windowsUser));
    }
}
