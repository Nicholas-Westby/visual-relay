using System.Diagnostics;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The two registry keys <see cref="WslVmPlatform.ThisMachine"/> reads, checked against Windows'
/// own tools on the machine running the suite. A wrong service key would read every Windows as
/// "platform off" and stop every launch there, so the keys are proven, not just written down.
/// </summary>
public sealed class WslVmPlatformTests
{
    /// <summary>What sc.exe exits with for a service that is not installed.</summary>
    private const int ServiceDoesNotExist = 1060;

    [Fact]
    public async Task ThisMachine_AgreesWithTheServiceManagerAndTheRegistryTool()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the virtual machine platform is a Windows service and a Windows key");

        var platform = WslVmPlatform.ThisMachine();

        Assert.Equal(await ExitCodeAsync("sc.exe", "query", "vmcompute") != ServiceDoesNotExist, platform.Running);
        // reg.exe exits 0 for a key that exists and 1 for one that does not.
        Assert.Equal(await ExitCodeAsync("reg.exe", "query",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") == 0,
            platform.RestartPending);
    }

    /// <summary>
    /// A key that cannot be read must never stop a machine whose WSL works: "not running"
    /// blocks every launch there, so an unreadable key reads as the answer that blocks nothing.
    /// </summary>
    [Fact]
    public void UnreadableKeys_ReadAsARunningPlatformWithNothingPending()
    {
        Assert.Equal(WslVmPlatform.Ready, WslVmPlatform.From(_ => null));
    }

    [Theory]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    public void TheServiceKeySaysRunning_AndTheServicingKeySaysRestartPending(
        bool serviceKey, bool servicingKey, bool running, bool restartPending)
    {
        var platform = WslVmPlatform.From(path => path.Contains("vmcompute", StringComparison.Ordinal) ? serviceKey : servicingKey);

        Assert.Equal(new WslVmPlatform(running, restartPending), platform);
    }

    [Fact]
    public void OffWindows_NothingIsReadAndNothingBlocks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the answer is the machine's own");

        Assert.Equal(WslVmPlatform.Ready, WslVmPlatform.ThisMachine());
    }

    private static async Task<int> ExitCodeAsync(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
