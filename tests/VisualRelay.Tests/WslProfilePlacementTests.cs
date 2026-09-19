using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Where the vr-guard profile lives on Windows: written from the Windows side
/// through the distro's UNC share, read by nono inside the distro by its Linux
/// path. Both paths from one call so they can never disagree.
/// </summary>
public sealed class WslProfilePlacementTests
{
    [Fact]
    public void For_HomeWithASpace_GivesTheUncWritePathAndTheLinuxPath()
    {
        var (writePath, linuxPath) = WslProfilePlacement.For("VrNoSuchDistro", "/home/a b");

        Assert.Equal(@"\\wsl.localhost\VrNoSuchDistro\home\a b\.config\visual-relay\vr-guard.json", writePath);
        Assert.Equal("/home/a b/.config/visual-relay/vr-guard.json", linuxPath);
    }

    [Fact]
    public void For_TrailingSlashHome_IsNormalised()
    {
        var (writePath, linuxPath) = WslProfilePlacement.For("VrNoSuchDistro", "/home/u/");

        Assert.Equal(@"\\wsl.localhost\VrNoSuchDistro\home\u\.config\visual-relay\vr-guard.json", writePath);
        Assert.Equal("/home/u/.config/visual-relay/vr-guard.json", linuxPath);
    }

    [Fact]
    public void For_RootUser_PlacesUnderRoot()
    {
        var (writePath, linuxPath) = WslProfilePlacement.For("VrNoSuchDistro", "/root");

        Assert.Equal(@"\\wsl.localhost\VrNoSuchDistro\root\.config\visual-relay\vr-guard.json", writePath);
        Assert.Equal("/root/.config/visual-relay/vr-guard.json", linuxPath);
    }

    [Fact]
    public void For_RelativeHome_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => WslProfilePlacement.For("VrNoSuchDistro", "home/u"));
    }
}
