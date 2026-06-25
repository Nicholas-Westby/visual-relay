using VisualRelay.Cli;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the Windows PATHEXT-aware probe in
/// <see cref="ProcessLauncher.ResolveOnPath"/>. The pure helper takes the PATH
/// and PATHEXT strings plus an injected <c>fileExists</c> predicate so the
/// Windows resolution is asserted on any OS. The failing case today: a bare
/// <c>git</c> probe must find <c>git.exe</c> only when PATHEXT is consulted.
/// </summary>
public sealed class ProcessLauncherOnPathTests
{
    private const string Dir = @"C:\tools\bin";
    private const string Pathext = ".COM;.EXE;.BAT;.CMD";

    // Case-insensitive existence of a single Windows executable, mimicking
    // NTFS/File.Exists semantics (PATHEXT is upper-case, files are lower-case).
    private static Func<string, bool> Exists(string fullPath) =>
        candidate => string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ResolveOnPath_BareName_WithPathext_FindsExe()
    {
        var found = ProcessLauncher.ResolveOnPath(
            Dir, Pathext, "git", Exists(Path.Combine(Dir, "git.exe")));

        Assert.True(found);
    }

    [Fact]
    public void ResolveOnPath_BareName_WithoutPathext_DoesNotFindExe()
    {
        // The bug today: no PATHEXT handling means a bare "git" never matches
        // "git.exe", so a present git is reported missing.
        var found = ProcessLauncher.ResolveOnPath(
            Dir, pathext: "", "git", Exists(Path.Combine(Dir, "git.exe")));

        Assert.False(found);
    }

    [Fact]
    public void ResolveOnPath_NameWithExplicitExtension_MatchesExactly()
    {
        var found = ProcessLauncher.ResolveOnPath(
            Dir, Pathext, "nono.exe", Exists(Path.Combine(Dir, "nono.exe")));

        Assert.True(found);
    }

    [Fact]
    public void ResolveOnPath_Missing_ReturnsFalse()
    {
        var found = ProcessLauncher.ResolveOnPath(
            Dir, Pathext, "swival", _ => false);

        Assert.False(found);
    }

    [Fact]
    public void ResolveOnPath_SearchesEveryPathEntry()
    {
        var pathEnv = @"C:\a" + Path.PathSeparator + @"C:\b";
        var found = ProcessLauncher.ResolveOnPath(
            pathEnv, Pathext, "git", Exists(@"C:\b\git.exe"));

        Assert.True(found);
    }
}
