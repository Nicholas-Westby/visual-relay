using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Every child Visual Relay starts through <see cref="ProcessCapture"/> runs without a
/// console window of its own. Measured on Windows 11 with Windows Terminal as the default
/// console host: the GUI app's wsl.exe children each opened a visible terminal window that
/// took focus, four at launch and nine during one bootstrap, because the app has no
/// console for them to share. A child told not to create a window gets a windowless console
/// of its own, whatever its parent has: no console window, and no other process on it.
/// </summary>
public sealed class ProcessCaptureConsoleWindowTests
{
    // The child prints its console window (0: none) and how many processes share its console.
    // The window alone cannot fail under vstest, which starts testhost with a windowless
    // console that a child inherits; an inheriting child shares it with testhost, though.
    // Measured on Windows 11: GetConsoleProcessList called straight from PowerShell returned
    // 0 (failure) whatever the parent; the C# wrapper with an [Out] buffer is what answers.
    private const string PrintConsoleWindowAndSharers =
        "Add-Type -Name Native -Namespace VrProbe -MemberDefinition "
        + "'[DllImport(\"kernel32.dll\")] public static extern System.IntPtr GetConsoleWindow(); "
        + "[DllImport(\"kernel32.dll\", SetLastError = true)] static extern uint GetConsoleProcessList([Out] uint[] list, uint count); "
        + "public static uint ConsoleProcessCount() { return GetConsoleProcessList(new uint[64], 64); }'; "
        + "'{0} {1}' -f [VrProbe.Native]::GetConsoleWindow().ToInt64(), [VrProbe.Native]::ConsoleProcessCount()";

    [Fact]
    public async Task RunAsync_OnWindows_StartsTheChildWithoutAConsoleWindow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Console windows exist only on Windows");

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            powershell, ["-NoProfile", "-NonInteractive", "-Command", PrintConsoleWindowAndSharers],
            Path.GetTempPath(), TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.False(timedOut);
        Assert.True(exitCode == 0, output);
        Assert.Equal("0 1", output.Trim());
    }
}
