using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// The Windows fallback to <c>APPDATA</c> reads the same environment as the rest of the
/// resolution. Measured on Windows: the test suite handed the view model an empty injected
/// environment, the fallback read the machine's real <c>%APPDATA%</c> anyway, and a settings
/// save rewrote the user's real .env (the file holding the API keys) with three Obsidian lines.
/// </summary>
public sealed class XdgConfigInjectedEnvironmentTests
{
    [Fact]
    public void AnInjectedEnvironmentWithNoConfigVariable_ResolvesNothing_OnWindowsToo()
    {
        Assert.Throws<InvalidOperationException>(() =>
            XdgConfig.ResolveConfigDir(new DictionaryEnvironmentAccessor(), isWindows: true));
    }

    [Fact]
    public void AnInjectedEnvironment_FallsBackToItsOwnAppData_OnWindows()
    {
        var env = new DictionaryEnvironmentAccessor { ["APPDATA"] = @"C:\Users\u\AppData\Roaming" };

        Assert.Equal(@"C:\Users\u\AppData\Roaming", XdgConfig.ResolveConfigDir(env, isWindows: true));
    }

    [Fact]
    public void AnInjectedXdgConfigHome_StillWinsOverAppData()
    {
        var env = new DictionaryEnvironmentAccessor
        {
            ["XDG_CONFIG_HOME"] = "/tmp/xdg",
            ["APPDATA"] = @"C:\Users\u\AppData\Roaming",
        };

        Assert.Equal("/tmp/xdg", XdgConfig.ResolveConfigDir(env, isWindows: true));
    }
}
