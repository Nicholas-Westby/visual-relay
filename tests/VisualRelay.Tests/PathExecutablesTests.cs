using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the canonical PATH executable resolver shared by the backend
/// venv probe, the git invoker, and the CLI gate probes. The pure
/// <see cref="PathExecutables.Resolve"/> takes PATH/PATHEXT plus an injected
/// existence predicate so both the Windows (PATHEXT) and Unix (bare-name) branches
/// are asserted on any OS, returning the full resolved path. Paths are built with
/// <see cref="Path.Combine(string, string)"/> so the host's separator is used, and
/// Windows comparisons are case-insensitive (the resolved extension comes from the
/// upper-case PATHEXT, and Windows paths are case-insensitive anyway).
/// </summary>
public sealed class PathExecutablesTests
{
    private static readonly string Dir = Path.Combine("C:", "tools", "bin");
    private const string Pathext = ".COM;.EXE;.BAT;.CMD";

    private static Func<string, bool> Only(string fullPath) =>
        c => string.Equals(c, fullPath, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Resolve_Windows_BareName_FindsExeViaPathext()
    {
        var target = Path.Combine(Dir, "uv.exe");
        var found = PathExecutables.Resolve("uv", Dir, Pathext, isWindows: true, Only(target));

        Assert.Equal(target, found, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_Windows_BareName_WithoutPathext_NotFound()
    {
        var found = PathExecutables.Resolve(
            "uv", Dir, pathext: "", isWindows: true, Only(Path.Combine(Dir, "uv.exe")));

        Assert.Null(found);
    }

    [Fact]
    public void Resolve_Windows_SearchesEveryEntry()
    {
        var dirA = Path.Combine("C:", "a");
        var dirB = Path.Combine("C:", "b");
        var target = Path.Combine(dirB, "git.exe");
        var path = dirA + ";" + dirB;

        var found = PathExecutables.Resolve("git", path, Pathext, isWindows: true, Only(target));

        Assert.Equal(target, found, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_Unix_BareName_FindsExecutable()
    {
        var binDir = Path.Combine("/usr", "bin");
        var target = Path.Combine(binDir, "git");

        var found = PathExecutables.Resolve("git", binDir, pathext: null, isWindows: false, Only(target));

        Assert.Equal(target, found);
    }

    [Fact]
    public void Resolve_Missing_ReturnsNull()
    {
        Assert.Null(PathExecutables.Resolve("nope", Dir, Pathext, isWindows: true, _ => false));
    }
}
