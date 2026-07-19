using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class WorkspaceVolumeTempDirTests
{
    [Fact]
    public void ExternalVolume_PathUnderVolume_ReturnsTemporaryItems()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");

        var result = WorkspaceVolumeTempDir.Resolve("/Volumes/Tera/dev/x");

        Assert.Equal("/Volumes/Tera/.TemporaryItems", result);
    }

    [Fact]
    public void ExternalVolume_VolumeRoot_ReturnsTemporaryItems()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");

        var result = WorkspaceVolumeTempDir.Resolve("/Volumes/Tera");

        Assert.Equal("/Volumes/Tera/.TemporaryItems", result);
    }

    [Fact]
    public void HomeDirectory_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");

        var result = WorkspaceVolumeTempDir.Resolve("/Users/nick/dev/x");

        Assert.Null(result);
    }

    [Fact]
    public void SystemTemp_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");

        var result = WorkspaceVolumeTempDir.Resolve("/private/tmp/wt");

        Assert.Null(result);
    }

    [Fact]
    public void TrailingSlash_HandledGracefully()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");

        var result = WorkspaceVolumeTempDir.Resolve("/Volumes/Tera/dev/");

        Assert.Equal("/Volumes/Tera/.TemporaryItems", result);
    }

    [Fact]
    public void BareVolumes_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");

        var result = WorkspaceVolumeTempDir.Resolve("/Volumes");

        Assert.Null(result);
    }

    [Fact]
    public void BareVolumesWithSlash_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");

        var result = WorkspaceVolumeTempDir.Resolve("/Volumes/");

        Assert.Null(result);
    }

    [Fact]
    public void NonMacOS_AlwaysReturnsNull()
    {
        Assert.SkipUnless(!OperatingSystem.IsMacOS(), "non-macOS guard");

        // On any non-macOS platform, including paths that happen to
        // start with /Volumes/, the helper must return null.
        var result = WorkspaceVolumeTempDir.Resolve("/Volumes/Tera/dev/x");

        Assert.Null(result);
    }
}
