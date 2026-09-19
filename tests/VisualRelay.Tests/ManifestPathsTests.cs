using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// The plan lists the files a task will touch and Author-tests lists the test files
/// it wrote; both are repo-relative by contract. A model that writes an absolute path
/// instead used to have its leading slash trimmed, leaving a relative name that exists
/// nowhere — the file then dropped out of the commit, the red gate and the test count
/// with nothing logged. An absolute path under the workspace now resolves to the name
/// the model meant, and any other one is a reported drop.
/// </summary>
public sealed class ManifestPathsTests
{
    [Fact]
    public void TryResolve_ARootedPathUnderTheRoot_IsMadeRelative()
    {
        Assert.True(ManifestPaths.TryResolve("/repo", "/repo/src/x.rs", out var repoRelative, out var rejection));
        Assert.Equal("src/x.rs", repoRelative);
        Assert.Null(rejection);
    }

    [Fact]
    public void TryResolve_AWindowsDriveForm_IsMadeRelativeToo()
    {
        // A drive letter is never a repo-relative first segment on any host, so this
        // resolves textually and the fact runs on macOS as well as Windows.
        Assert.True(ManifestPaths.TryResolve(@"C:\repo", @"C:\repo\src\x.cs", out var repoRelative, out _));
        Assert.Equal("src/x.cs", repoRelative);
    }

    /// <summary>
    /// The root must never go through <c>Path.GetFullPath</c>: on Windows that
    /// resolves a POSIX-rooted path against the CURRENT DRIVE, so <c>/repo</c> became
    /// <c>C:\repo</c> while the entry beside it stayed textual, and every rooted entry
    /// was rejected as outside the workspace — the exact case this exists to rescue.
    /// Measured on a real Windows box: <c>[IO.Path]::GetFullPath('/repo')</c> is
    /// <c>C:\repo</c>.
    /// </summary>
    [Fact]
    public void TryResolve_APosixRootedPath_ResolvesTheSameOnEveryHost()
    {
        Assert.True(ManifestPaths.TryResolve("/repo", "/repo/src/x.rs", out var repoRelative, out _));
        Assert.Equal("src/x.rs", repoRelative);
        Assert.NotEqual("C:/repo/src/x.rs", repoRelative);
    }

    /// <summary>
    /// On Windows the two sides live in different namespaces: Visual Relay holds the
    /// workspace as a UNC path into the distro, while the stage runs INSIDE that
    /// distro and writes distro-absolute paths. They name one tree and match under
    /// neither spelling alone, so the root offers both.
    /// </summary>
    [Fact]
    public void TryResolve_ADistroPathUnderAUncRoot_IsMadeRelative()
    {
        Assert.True(ManifestPaths.TryResolve(
            @"\\wsl.localhost\VrNoSuchDistro\home\alice\repo",
            "/home/alice/repo/src/x.rs",
            out var repoRelative,
            out var rejection));

        Assert.Equal("src/x.rs", repoRelative);
        Assert.Null(rejection);
    }

    [Fact]
    public void TryResolve_AUncPathUnderAUncRoot_IsMadeRelative()
    {
        Assert.True(ManifestPaths.TryResolve(
            @"\\wsl.localhost\VrNoSuchDistro\home\alice\repo",
            @"\\wsl.localhost\VrNoSuchDistro\home\alice\repo\src\x.rs",
            out var repoRelative,
            out _));

        Assert.Equal("src/x.rs", repoRelative);
    }

    [Fact]
    public void TryResolve_ADistroPathOutsideAUncRoot_IsStillRejected()
    {
        Assert.False(ManifestPaths.TryResolve(
            @"\\wsl.localhost\VrNoSuchDistro\home\alice\repo",
            "/home/alice/other/x.rs",
            out _,
            out var rejection));

        Assert.Equal("absolute path outside the workspace", rejection);
    }

    /// <summary>
    /// The shape a confused model on the Windows arm is most likely to produce: a
    /// path that IS reachable from inside the distro but is not in the workspace.
    /// Accepting the distro's spelling of the root must not accept everything the
    /// distro can see.
    /// </summary>
    [Fact]
    public void TryResolve_ADrvFsPathUnderAUncRoot_IsRejected()
    {
        Assert.False(ManifestPaths.TryResolve(
            @"\\wsl.localhost\VrNoSuchDistro\home\alice\repo",
            "/mnt/c/dev/elsewhere/x.cs",
            out _,
            out var rejection));

        Assert.Equal("absolute path outside the workspace", rejection);
    }

    /// <summary>
    /// The escape has to be refused from the DISTRO spelling too, not just the
    /// POSIX-rooted one: the two spellings are separate entry paths into the same
    /// comparison, and this is the case where a mistake would be worst. On a real
    /// Windows arm `/home/&lt;user&gt;/repo/../.ssh/id_rsa` resolves to an actual private
    /// key, and it is a plausible thing for a confused model to emit.
    /// </summary>
    [Fact]
    public void TryResolve_ADotDotEscapeFromTheDistroSpelling_IsRejected()
    {
        Assert.False(ManifestPaths.TryResolve(
            @"\\wsl.localhost\VrNoSuchDistro\home\alice\repo",
            "/home/alice/repo/../.ssh/id_rsa",
            out _,
            out var rejection));

        Assert.Equal("absolute path outside the workspace", rejection);
    }

    [Fact]
    public void TryResolve_ARootedPathOutsideTheRoot_IsRejectedWithTheReason()
    {
        Assert.False(ManifestPaths.TryResolve("/repo", "/elsewhere/src/x.rs", out var repoRelative, out var rejection));
        Assert.Equal(string.Empty, repoRelative);
        Assert.Equal("absolute path outside the workspace", rejection);
    }

    [Fact]
    public void TryResolve_ADotDotEscapeOutOfTheRoot_IsRejected()
    {
        Assert.False(ManifestPaths.TryResolve("/repo", "/repo/../secrets.env", out _, out var rejection));
        Assert.Equal("absolute path outside the workspace", rejection);
    }

    [Theory]
    [InlineData("+src/new.rs", "src/new.rs")]
    [InlineData("./src/x.rs", "src/x.rs")]
    [InlineData(@"src\x.rs", "src/x.rs")]
    [InlineData("src/dir/", "src/dir")]
    public void TryResolve_ARelativePath_IsNormalizedAsBefore(string entry, string expected)
    {
        Assert.True(ManifestPaths.TryResolve("/repo", entry, out var repoRelative, out var rejection));
        Assert.Equal(expected, repoRelative);
        Assert.Null(rejection);
    }

    [Fact]
    public void TryResolve_APlusOnAnAbsolutePath_StillResolves()
    {
        // The Plan stage marks a new file with a leading '+'.
        Assert.True(ManifestPaths.TryResolve("/repo", "+/repo/src/new.rs", out var repoRelative, out _));
        Assert.Equal("src/new.rs", repoRelative);
    }

    [Fact]
    public void NormalizeRepoRelativePath_KeepsALeadingSlash()
    {
        // Trimming it is what turned /Users/me/repo/src/x.rs into a name that exists
        // nowhere. Keeping it means a rooted path can never pass as relative again.
        Assert.Equal("/repo/src/x.rs", WorktreeFilter.NormalizeRepoRelativePath("/repo/src/x.rs"));
    }

    [Fact]
    public void ResolveAll_SplitsTheResolvedFromTheDropped()
    {
        var (resolved, dropped) = ManifestPaths.ResolveAll(
            "/repo", ["/repo/src/a.rs", "src/b.rs", "/elsewhere/c.rs", ""]);

        Assert.Equal(["src/a.rs", "src/b.rs"], resolved);
        var drop = Assert.Single(dropped);
        Assert.Equal("/elsewhere/c.rs", drop.Entry);
        Assert.Equal("absolute path outside the workspace", drop.Reason);
    }
}
