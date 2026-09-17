using System.Text;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// <see cref="WslProber"/> against a scripted wsl.exe: the delegate replies with
/// canned output per argv, so every fact the probe records, the exact argv of
/// every step, and the order they run in are pinned without spawning anything.
/// </summary>
public sealed class WslProberTests
{
    private const string Exe = ScriptedWsl.Exe;
    private const string ListOutput = ScriptedWsl.ListOutput;
    private const string SandboxCheckPassed = ScriptedWsl.SandboxCheckPassed;

    private static ScriptedWsl HealthyUbuntu() => ScriptedWsl.HealthyUbuntu();

    [Fact]
    public async Task HealthyMachine_RecordsEveryFactAndIsUsable()
    {
        var wsl = HealthyUbuntu();

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, requestedDistro: null, wslExePath: Exe, CancellationToken.None);

        Assert.True(probe.IsUsable, probe.Diagnostics);
        Assert.True(probe.WslExeFound);
        Assert.Equal(Exe, probe.WslExePath);
        Assert.Equal(["Ubuntu", "Debian"], probe.Distros.Select(d => d.Name));
        Assert.Null(probe.RequestedDistro);
        Assert.Equal("Ubuntu", probe.DistroName);
        Assert.True(probe.IsWsl2);
        Assert.Equal("5.15.167.4-microsoft-standard-WSL2", probe.KernelRelease);
        Assert.Equal("/usr/local/bin/nono", probe.NonoPath);
        Assert.Equal("nono 0.75.0", probe.NonoVersion);
        Assert.True(probe.LandlockActive);
        Assert.Equal("/home/alice", probe.DistroHome);
        Assert.Equal(ScriptedWsl.UserPath, probe.UserPath);
        Assert.Equal("/usr/bin/git", probe.GitPath);
    }

    [Fact]
    public async Task HealthyMachine_RunsExactlyTheEightDocumentedStepsInOrder()
    {
        var wsl = HealthyUbuntu();

        await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        string[][] expected =
        [
            ["-l", "-v"],
            ["-d", "Ubuntu", "--exec", "uname", "-r"],
            ["-d", "Ubuntu", "--exec", "sh", "-lc", "command -v nono"],
            ["-d", "Ubuntu", "--exec", "/usr/local/bin/nono", "--version"],
            ["-d", "Ubuntu", "--exec", "env", "NONO_NO_UPDATE_CHECK=1", "/usr/local/bin/nono", "setup", "--check-only"],
            ["-d", "Ubuntu", "--exec", "sh", "-lc", "printf %s \"$HOME\""],
            ["-d", "Ubuntu", "--exec", "sh", "-c", WslProber.LoginPathScript],
            ["-d", "Ubuntu", "--exec", "env", $"PATH={ScriptedWsl.UserPath}", "sh", "-c", "command -v git"],
        ];
        Assert.Equal(expected.Length, wsl.Calls.Count);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], wsl.Calls[i]);
    }

    [Fact]
    public async Task RequestedDistroNotInstalled_StopsAfterTheListing()
    {
        var wsl = HealthyUbuntu();

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, requestedDistro: "Fedora", Exe, CancellationToken.None);

        Assert.False(probe.IsUsable);
        Assert.Equal("Fedora", probe.RequestedDistro);
        Assert.Null(probe.DistroName);
        Assert.Equal(2, probe.Distros.Count);
        Assert.Single(wsl.Calls);
    }

    [Fact]
    public async Task RequestedDistro_MatchesCaseInsensitivelyAndOverridesTheDefault()
    {
        var wsl = new ScriptedWsl()
            .On("-l -v", 0, ListOutput)
            .On("-d Debian --exec uname -r", 0, "4.4.0-19041-Microsoft\n");

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, requestedDistro: "debian", Exe, CancellationToken.None);

        Assert.Equal("Debian", probe.DistroName);
        Assert.Equal("debian", probe.RequestedDistro);
        Assert.All(wsl.Calls.Skip(1), argv => Assert.Equal("Debian", argv[1]));
    }

    [Fact]
    public async Task Wsl1Distro_IsNotWsl2()
    {
        var wsl = new ScriptedWsl()
            .On("-l -v", 0, "  NAME      STATE     VERSION\n* Debian    Stopped   1\n")
            .On("-d Debian --exec uname -r", 0, "4.4.0-19041-Microsoft\n");

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        Assert.Equal("Debian", probe.DistroName);
        Assert.False(probe.IsWsl2);
        Assert.Equal("4.4.0-19041-Microsoft", probe.KernelRelease);
        Assert.False(probe.IsUsable);
    }

    [Fact]
    public async Task ListedAsVersion2_CountsAsWsl2EvenWhenACustomKernelLacksTheMarker()
    {
        // A custom `kernel=` in .wslconfig is still a WSL2 VM; the Landlock check,
        // not the kernel name, is what decides whether such a kernel is usable.
        var wsl = HealthyUbuntu().On("-d Ubuntu --exec uname -r", 0, "6.6.87-custom\n");

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        Assert.True(probe.IsWsl2);
        Assert.Equal("6.6.87-custom", probe.KernelRelease);
    }

    [Fact]
    public async Task NonoMissing_LeavesNonoAndLandlockUnprobedButStillProbesHome()
    {
        var wsl = HealthyUbuntu().On("-d Ubuntu --exec sh -lc command -v nono", 1, "");

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        Assert.Null(probe.NonoPath);
        Assert.Null(probe.NonoVersion);
        Assert.DoesNotContain(wsl.Calls, argv => argv.Contains("--version") || argv.Contains("setup"));
        Assert.False(probe.LandlockActive);
        Assert.Equal("/home/alice", probe.DistroHome);
        Assert.False(probe.IsUsable);
    }

    [Fact]
    public async Task NonosSandboxCheckFails_LandlockIsInactive_AndTheReasonIsKept()
    {
        var wsl = HealthyUbuntu().On("-d Ubuntu --exec env NONO_NO_UPDATE_CHECK=1 /usr/local/bin/nono setup --check-only", 1,
            "[2/4] Testing sandbox support...\nnono: Setup error: Landlock is not available: No supported Landlock ABI detected\n");

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        Assert.False(probe.LandlockActive);
        Assert.False(probe.IsUsable);
        Assert.Contains("No supported Landlock ABI detected", probe.Diagnostics);
    }

    [Fact]
    public async Task SecurityfsUnmounted_DoesNotDecideLandlock()
    {
        // Measured on WSL 2.7.14 with Ubuntu 26.04 (systemd): securityfs is not mounted,
        // so /sys/kernel/security/lsm does not exist, yet Landlock is up (ABI 7).
        var wsl = HealthyUbuntu();

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        Assert.True(probe.LandlockActive);
        Assert.DoesNotContain(wsl.Calls, argv => argv.Any(a => a.Contains("/sys/kernel/security")));
    }

    [Fact]
    public async Task NonoPathAndHome_TakeTheLastAbsoluteLine_IgnoringLoginShellNoise()
    {
        var wsl = HealthyUbuntu()
            .On("-d Ubuntu --exec sh -lc command -v nono", 0, "Welcome to Ubuntu\n/home/alice/.cargo/bin/nono\n")
            .On("-d Ubuntu --exec /home/alice/.cargo/bin/nono --version", 0, "nono 0.75.0")
            .On("-d Ubuntu --exec env NONO_NO_UPDATE_CHECK=1 /home/alice/.cargo/bin/nono setup --check-only", 0, SandboxCheckPassed)
            .On("-d Ubuntu --exec sh -lc printf %s \"$HOME\"", 0, "Welcome to Ubuntu\n/home/alice");

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        Assert.Equal("/home/alice/.cargo/bin/nono", probe.NonoPath);
        Assert.Equal("/home/alice", probe.DistroHome);
        Assert.True(probe.IsUsable);
    }

    [Fact]
    public async Task WslExeMissing_RunsNothing()
    {
        var wsl = HealthyUbuntu();

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, wslExePath: null, CancellationToken.None);

        Assert.False(probe.WslExeFound);
        Assert.Null(probe.WslExePath);
        Assert.Empty(wsl.Calls);
        Assert.False(probe.IsUsable);
    }

    [Fact]
    public async Task ListingFails_ReportsNoDistroWithoutProbingFurther()
    {
        var wsl = new ScriptedWsl()
            .On("-l -v", -1, "Windows Subsystem for Linux has no installed distributions.\n")
            .On("--version", 0, "WSL version: 2.7.14.0\nKernel version: 6.18.33.2-2\n");

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        Assert.True(probe.WslExeFound);
        Assert.False(probe.WslPlatformMissing);
        Assert.Empty(probe.Distros);
        Assert.Null(probe.DistroName);
        Assert.Equal([["-l", "-v"], ["--version"]], wsl.Calls);
        Assert.Contains("no installed distributions", probe.Diagnostics);
    }

    [Fact]
    public async Task InboxStubWithoutWsl_FlagsThePlatformMissing_AndReportsItsNoticeReadably()
    {
        // Measured on Windows 11 25H2 before WSL was installed: the inbox wsl.exe
        // answers every command with this notice, exit 1, in UTF-16LE whatever
        // WSL_UTF8 says, so the UTF-8 reader sees a NUL after every character.
        const string notice = "The Windows Subsystem for Linux is not installed. You can install by running 'wsl.exe --install'.\r\n";
        var asRead = Encoding.UTF8.GetString(Encoding.Unicode.GetBytes(notice));
        var wsl = new ScriptedWsl().On("-l -v", 1, asRead).On("--version", 1, asRead);

        var probe = await WslProber.ProbeAsync(wsl.RunAsync, null, Exe, CancellationToken.None);

        Assert.True(probe.WslPlatformMissing);
        Assert.False(probe.IsUsable);
        Assert.Equal([["-l", "-v"], ["--version"]], wsl.Calls);
        Assert.Contains("The Windows Subsystem for Linux is not installed.", probe.Diagnostics);
        Assert.DoesNotContain('\0', probe.Diagnostics!);
    }

    [Fact]
    public async Task RunnerThrows_ProbeIsUnusableAndCarriesTheError()
    {
        var probe = await WslProber.ProbeAsync(
            (_, _) => throw new InvalidOperationException("wsl.exe exploded"),
            null, Exe, CancellationToken.None);

        Assert.True(probe.WslExeFound);
        Assert.False(probe.IsUsable);
        Assert.Contains("wsl.exe exploded", probe.Diagnostics);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WslProber.ProbeAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult((0, "")); },
            null, Exe, cts.Token));
    }
}
