using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Cross-platform unit tests for <see cref="BackendPaths"/>'s data-home
/// resolution Windows branch, driven through an injected "is-windows +
/// localappdata" seam so the Windows fallback is asserted on any OS. The XDG
/// layout must be byte-for-byte unchanged when those vars are set; only when
/// neither <c>XDG_DATA_HOME</c> nor <c>HOME</c> is set does the resolver fall
/// back to <c>%LOCALAPPDATA%\visual-relay</c> on Windows.
/// </summary>
public sealed class BackendPathsWindowsTests
{
    private const string LocalAppData = @"C:\Users\tester\AppData\Local";

    [Fact]
    public void Combine_NeitherSet_OnWindows_FallsBackToLocalAppData()
    {
        var dataHome = BackendPaths.Combine(
            xdgDataHome: null, home: null, isWindows: true, localAppData: LocalAppData);

        Assert.Equal(Path.Combine(LocalAppData, "visual-relay"), dataHome);
    }

    [Fact]
    public void Combine_NeitherSet_OffWindows_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BackendPaths.Combine(
                xdgDataHome: null, home: null, isWindows: false, localAppData: LocalAppData));
    }

    [Fact]
    public void Combine_XdgSet_OnWindows_StillPrefersXdgLayout()
    {
        var dataHome = BackendPaths.Combine(
            xdgDataHome: "/explicit/data", home: "/home/u", isWindows: true, localAppData: LocalAppData);

        Assert.Equal(Path.Combine("/explicit/data", "visual-relay"), dataHome);
    }

    [Fact]
    public void Combine_HomeSet_OnWindows_StillPrefersHomeLocalShare()
    {
        var dataHome = BackendPaths.Combine(
            xdgDataHome: null, home: "/home/u", isWindows: true, localAppData: LocalAppData);

        Assert.Equal(Path.Combine("/home/u", ".local", "share", "visual-relay"), dataHome);
    }
}
