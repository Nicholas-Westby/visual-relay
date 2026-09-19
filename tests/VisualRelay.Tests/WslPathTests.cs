using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Pure path translation between the Windows view of a WSL distro
/// (<c>\\wsl$\VrNoSuchD\...</c> and <c>\\wsl.localhost\VrNoSuchD\...</c>), the Linux path VR hands
/// to <c>wsl.exe</c>, and the DrvFs mount a Windows drive appears under inside the
/// distro. One place, both directions, spaces and non-ASCII preserved verbatim.
/// </summary>
public sealed class WslPathTests
{
    // ── UNC → (distro, linux path) ────────────────────────────────────────

    [Theory]
    [InlineData(@"\\wsl$\VrNoSuchDistro\home\u\repo", "VrNoSuchDistro", "/home/u/repo")]
    [InlineData(@"\\wsl.localhost\VrNoSuchDistro\home\u\repo", "VrNoSuchDistro", "/home/u/repo")]
    [InlineData(@"\\WSL.LOCALHOST\VrNoSuchDistro-22.04\home\u\repo", "VrNoSuchDistro-22.04", "/home/u/repo")]
    [InlineData(@"\\wsl$\VrNoSuchD\a b\ü", "VrNoSuchD", "/a b/ü")]
    [InlineData(@"\\wsl.localhost\VrNoSuchD\a b\ü", "VrNoSuchD", "/a b/ü")]
    [InlineData(@"\\wsl.localhost\VrNoSuchD\a b\ü\", "VrNoSuchD", "/a b/ü")]
    [InlineData("//wsl$/VrNoSuchD/a b/ü", "VrNoSuchD", "/a b/ü")]
    [InlineData(@"\\wsl$\VrNoSuchD/a b\ü//", "VrNoSuchD", "/a b/ü")]
    [InlineData(@"\\wsl$\VrNoSuchD", "VrNoSuchD", "/")]
    [InlineData(@"\\wsl$\VrNoSuchD\", "VrNoSuchD", "/")]
    public void TryParseUnc_AcceptsBothPrefixes_SpacesNonAsciiAndEitherSlash(
        string path, string distro, string linux)
    {
        Assert.True(WslPath.TryParseUnc(path, out var parsedDistro, out var parsedLinux), path);
        Assert.Equal(distro, parsedDistro);
        Assert.Equal(linux, parsedLinux);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\Users\u\repo")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"wsl$\D\x")]
    [InlineData(@"\wsl$\D\x")]
    [InlineData(@"\\wsl$")]
    [InlineData(@"\\wsl$\")]
    [InlineData(@"\\wsl$\\x")]
    [InlineData(@"\\wsl$\VrNoSuchD\a\..\b")]
    [InlineData(@"\\wsl.localhost\VrNoSuchD\..")]
    [InlineData(@"\\wsl$\..\x")]
    public void TryParseUnc_RejectsRelativePathsEmptyDistroAndDotDot(string path)
    {
        Assert.False(WslPath.TryParseUnc(path, out var distro, out var linux), path);
        Assert.Equal(string.Empty, distro);
        Assert.Equal(string.Empty, linux);
    }

    // ── (distro, linux path) → UNC ────────────────────────────────────────

    [Fact]
    public void ToUnc_UsesTheLocalhostFormWithBackslashes()
    {
        Assert.Equal(@"\\wsl.localhost\VrNoSuchD\a b\ü", WslPath.ToUnc("VrNoSuchD", "/a b/ü"));
    }

    [Fact]
    public void ToUnc_DistroRootAndTrailingSlash_HaveNoTrailingSeparator()
    {
        Assert.Equal(@"\\wsl.localhost\VrNoSuchD", WslPath.ToUnc("VrNoSuchD", "/"));
        Assert.Equal(@"\\wsl.localhost\VrNoSuchD\x", WslPath.ToUnc("VrNoSuchD", "/x/"));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("/a/../b")]
    public void ToUnc_RejectsRelativeOrDotDotLinuxPaths(string linux)
    {
        Assert.Throws<ArgumentException>(() => WslPath.ToUnc("VrNoSuchD", linux));
    }

    [Fact]
    public void ToUnc_RejectsAnEmptyDistro()
    {
        Assert.Throws<ArgumentException>(() => WslPath.ToUnc("", "/x"));
    }

    [Fact]
    public void UncToLinuxToUnc_RoundTrips()
    {
        // The folder picker may hand back the legacy \\wsl$ form; VR stores the
        // Linux path and re-renders the modern \\wsl.localhost form, and parsing
        // that again must land on the same facts.
        const string picked = @"\\wsl$\VrNoSuchDistro\home\alice\my repo\ü";
        Assert.True(WslPath.TryParseUnc(picked, out var distro, out var linux));

        var unc = WslPath.ToUnc(distro, linux);

        Assert.Equal(@"\\wsl.localhost\VrNoSuchDistro\home\alice\my repo\ü", unc);
        Assert.True(WslPath.TryParseUnc(unc, out var distro2, out var linux2));
        Assert.Equal((distro, linux), (distro2, linux2));
    }

    // ── Windows drive → DrvFs mount ───────────────────────────────────────

    [Theory]
    [InlineData(@"C:\x y\ü", "/mnt/c/x y/ü")]
    [InlineData(@"c:\repo", "/mnt/c/repo")]
    [InlineData(@"D:\", "/mnt/d")]
    [InlineData("C:/x/y", "/mnt/c/x/y")]
    [InlineData(@"C:\x\", "/mnt/c/x")]
    public void TryDriveToMnt_LowercasesTheDriveLetter(string windows, string linux)
    {
        Assert.True(WslPath.TryDriveToMnt(windows, out var mnt), windows);
        Assert.Equal(linux, mnt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"x\y")]
    [InlineData("C:x")]
    [InlineData("C:")]
    [InlineData(@"\x")]
    [InlineData("/home/u")]
    [InlineData(@"\\wsl$\VrNoSuchD\x")]
    [InlineData(@"\\server\share")]
    [InlineData(@"1:\x")]
    public void TryDriveToMnt_RejectsRelativeAndUncPaths(string windows)
    {
        Assert.False(WslPath.TryDriveToMnt(windows, out var mnt), windows);
        Assert.Equal(string.Empty, mnt);
    }

    // ── DrvFs detection ───────────────────────────────────────────────────

    [Theory]
    [InlineData("/mnt/c", true)]
    [InlineData("/mnt/c/", true)]
    [InlineData("/mnt/c/Users/x/repo", true)]
    [InlineData("/mnt/D/x", true)]
    [InlineData("/mnt", false)]
    [InlineData("/mnt/", false)]
    [InlineData("/mnt/wsl/x", false)]
    [InlineData("/mnt/cc/x", false)]
    [InlineData("/mntx/c", false)]
    [InlineData("/home/u/repo", false)]
    [InlineData("mnt/c", false)]
    [InlineData("", false)]
    public void IsMntPath_TrueOnlyForASingleDriveLetterUnderMnt(string linux, bool expected)
    {
        Assert.Equal(expected, WslPath.IsMntPath(linux));
    }
}
