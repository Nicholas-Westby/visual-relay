using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Every child Visual Relay starts through <see cref="ProcessCapture"/> runs without a
/// console window of its own. Measured on Windows 11 with Windows Terminal as the default
/// console host: the GUI app's wsl.exe children each opened a visible terminal window that
/// took focus, four at launch and nine during one bootstrap, because the app has no
/// console for them to share. A child told not to create a window reports no console window.
/// </summary>
public sealed class ProcessCaptureConsoleWindowTests
{
    // The child asks Windows for its console window; 0 means it has none.
    private const string PrintConsoleWindow =
        "Add-Type -Name Native -Namespace VrProbe -MemberDefinition "
        + "'[DllImport(\"kernel32.dll\")] public static extern System.IntPtr GetConsoleWindow();'; "
        + "[VrProbe.Native]::GetConsoleWindow().ToInt64()";

    [Fact]
    public async Task RunAsync_OnWindows_StartsTheChildWithoutAConsoleWindow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Console windows exist only on Windows");

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            powershell, ["-NoProfile", "-NonInteractive", "-Command", PrintConsoleWindow],
            Path.GetTempPath(), TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.False(timedOut);
        Assert.True(exitCode == 0, output);
        Assert.Equal("0", output.Trim());
    }
}
