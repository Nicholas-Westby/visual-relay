using System.Runtime.CompilerServices;

namespace VisualRelay.Tests;

/// <summary>
/// Runs once when the test assembly loads — before ANY test — and redirects
/// <c>XDG_CONFIG_HOME</c> to a unique temp directory so that even a test
/// that constructs <c>MainWindowViewModel</c> with a null accessor can never
/// resolve the real per-user <c>~/.config/visual-relay/.env</c>.
/// </summary>
internal static class TestModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "vr-test-xdg-config",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", tempDir);
    }
}
