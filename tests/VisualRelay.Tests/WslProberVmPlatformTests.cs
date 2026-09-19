using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The probe's reading of the virtual machine WSL2 runs in. On 2026-09-19 a reset removed WSL's
/// app for the user and turned the Virtual Machine Platform off, and WSL 2.7.14 on Windows 11
/// 25H2 still answered <c>--version</c> with exit 0 from the files left behind; the distro install
/// setup then ran exited 0 having installed nothing. So the probe asks Windows as well.
/// </summary>
public sealed class WslProberVmPlatformTests
{
    private const string Exe = ScriptedWsl.Exe;

    private const string NoDistros =
        "Windows Subsystem for Linux has no installed distributions.\n"
        + "You can resolve this by installing a distribution with the instructions below:\n\n"
        + "Use 'wsl.exe --list --online' to list available distributions\n"
        + "and 'wsl.exe --install <Distro>' to install.\n";

    private static readonly WslVmPlatform Off = new(Running: false, RestartPending: false);

    [Fact]
    public async Task WslThatAnswersVersion_WithoutItsVmPlatform_IsFlagged()
    {
        var wsl = InstalledWithoutADistro().WithVmPlatform(Off);

        var probe = await ProbeAsync(wsl);

        Assert.False(probe.WslPlatformMissing);
        Assert.True(probe.VmPlatformMissing);
        Assert.False(probe.RestartPending);
        Assert.Contains("Virtual Machine Platform", probe.Diagnostics);
    }

    [Fact]
    public async Task APendingRestart_IsRecordedWithTheMissingPlatform()
    {
        var wsl = InstalledWithoutADistro().WithVmPlatform(Off with { RestartPending = true });

        var probe = await ProbeAsync(wsl);

        Assert.True(probe.VmPlatformMissing);
        Assert.True(probe.RestartPending);
    }

    /// <summary>Without the platform no WSL2 distro can start, so every call into one would only fail.</summary>
    [Fact]
    public async Task AListedDistro_IsNotProbed_WhileThePlatformIsMissing()
    {
        var wsl = ScriptedWsl.HealthyUbuntu().WithVmPlatform(Off);

        var probe = await ProbeAsync(wsl);

        Assert.True(probe.VmPlatformMissing);
        Assert.Equal([["-l", "-v"]], wsl.Calls);
        Assert.False(probe.IsUsable);
    }

    [Fact]
    public async Task ARunningPlatform_LeavesAHealthyMachineUsable()
    {
        var wsl = ScriptedWsl.HealthyUbuntu();

        var probe = await ProbeAsync(wsl);

        Assert.Equal(1, wsl.VmPlatformReads);
        Assert.False(probe.VmPlatformMissing);
        Assert.True(probe.IsUsable);
    }

    /// <summary>A missing WSL says it all: installing WSL is what brings the platform too.</summary>
    [Fact]
    public async Task TheInboxStub_IsReportedAsMissingWsl_NotAsAMissingPlatform()
    {
        var wsl = new ScriptedWsl()
            .On("-l -v", 1, "The Windows Subsystem for Linux is not installed.\n")
            .On("--version", 1, "The Windows Subsystem for Linux is not installed.\n")
            .WithVmPlatform(Off);

        var probe = await ProbeAsync(wsl);

        Assert.True(probe.WslPlatformMissing);
        Assert.False(probe.VmPlatformMissing);
    }

    private static ScriptedWsl InstalledWithoutADistro() => new ScriptedWsl()
        .On("-l -v", -1, NoDistros)
        .On("--version", 0, "WSL version: 2.7.14.0\nKernel version: 6.18.33.2-2\n");

    private static Task<WslProbe> ProbeAsync(ScriptedWsl wsl) =>
        WslProber.ProbeAsync(wsl.RunAsync, null, Exe, wsl.ReadVmPlatform, CancellationToken.None);
}
