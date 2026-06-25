using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Cross-platform unit tests for <see cref="XdgConfig"/>'s config-directory
/// resolution, driving the Windows branch through an injected
/// "is-windows + appdata" seam so the Windows fallback is asserted on any OS
/// (macOS/Linux CI included). XDG/HOME precedence must be preserved everywhere;
/// only when neither is set does the resolver fall back to <c>%APPDATA%</c> on
/// Windows (and still throw off Windows).
/// </summary>
public sealed class XdgConfigTests
{
    private const string AppData = @"C:\Users\tester\AppData\Roaming";

    [Fact]
    public void Resolve_NeitherSet_OnWindows_FallsBackToAppData()
    {
        var dir = XdgConfig.ResolveConfigDir(
            xdgConfigHome: null, home: null, isWindows: true, appData: AppData);

        Assert.Equal(AppData, dir);
    }

    [Fact]
    public void Resolve_NeitherSet_OffWindows_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            XdgConfig.ResolveConfigDir(
                xdgConfigHome: null, home: null, isWindows: false, appData: AppData));
    }

    [Fact]
    public void Resolve_XdgSet_OnWindows_StillPrefersXdg()
    {
        var dir = XdgConfig.ResolveConfigDir(
            xdgConfigHome: "/explicit/xdg", home: "/home/u", isWindows: true, appData: AppData);

        Assert.Equal("/explicit/xdg", dir);
    }

    [Fact]
    public void Resolve_HomeSet_OnWindows_StillPrefersHomeDotConfig()
    {
        var dir = XdgConfig.ResolveConfigDir(
            xdgConfigHome: null, home: "/home/u", isWindows: true, appData: AppData);

        Assert.Equal(Path.Combine("/home/u", ".config"), dir);
    }

    [Fact]
    public void Resolve_NeitherSet_OnWindows_NoAppData_Throws()
    {
        // Windows but %APPDATA% itself is unset/empty — nothing to fall back to.
        Assert.Throws<InvalidOperationException>(() =>
            XdgConfig.ResolveConfigDir(
                xdgConfigHome: null, home: null, isWindows: true, appData: ""));
    }
}
