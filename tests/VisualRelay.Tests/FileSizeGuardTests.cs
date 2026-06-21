using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="FileSizeGuard"/> — the C# port of
/// <c>tools/guards/check-file-size.sh</c>. The guard flags <c>*.cs</c>/<c>*.axaml</c>
/// files under the source roots whose line count exceeds the limit (default 300,
/// overridable via <c>VISUAL_RELAY_FILE_LINE_LIMIT</c>), excluding <c>bin/</c>/<c>obj/</c>.
/// Written TDD: these must fail against absent code.
/// </summary>
public sealed class FileSizeGuardTests
{
    // ── Pure decision: FindViolations over (path, lineCount) pairs ───────

    [Fact]
    public void NoFilesOverLimit_ReturnsEmpty()
    {
        var files = new (string Path, int Lines)[]
        {
            ("src/A.cs", 1),
            ("src/B.cs", 300), // exactly at limit, not over
            ("tests/C.axaml", 299),
        };

        var violations = FileSizeGuard.FindViolations(files, 300);

        Assert.Empty(violations);
    }

    [Fact]
    public void FileOverLimit_IsReported()
    {
        var files = new (string Path, int Lines)[]
        {
            ("src/Big.cs", 301),
        };

        var violations = FileSizeGuard.FindViolations(files, 300);

        Assert.Single(violations);
        Assert.Equal("src/Big.cs", violations[0].Path);
        Assert.Equal(301, violations[0].Lines);
        Assert.Equal(300, violations[0].Limit);
    }

    [Fact]
    public void EnvOverridableLimit_LowersThreshold()
    {
        var files = new (string Path, int Lines)[]
        {
            ("src/Mid.cs", 100),
        };

        // With a limit of 50, a 100-line file violates.
        var violations = FileSizeGuard.FindViolations(files, 50);

        Assert.Single(violations);
        Assert.Equal(100, violations[0].Lines);
        Assert.Equal(50, violations[0].Limit);
    }

    [Fact]
    public void MultipleViolations_OrderedByPath()
    {
        var files = new (string Path, int Lines)[]
        {
            ("zzz/Last.cs", 400),
            ("aaa/First.cs", 500),
            ("mmm/Middle.cs", 350),
        };

        var violations = FileSizeGuard.FindViolations(files, 300);

        Assert.Equal(3, violations.Count);
        Assert.Equal("aaa/First.cs", violations[0].Path);
        Assert.Equal("mmm/Middle.cs", violations[1].Path);
        Assert.Equal("zzz/Last.cs", violations[2].Path);
    }

    // ── Enumeration: roots + extensions + bin/obj exclusion ──────────────

    [Fact]
    public void Enumerate_FlagsCsAndAxaml_ExcludesBinObj_OverLimit()
    {
        using var dir = new TempDir();
        // A .cs and a .axaml over the limit under the roots.
        WriteLines(dir.Root, "src/Big.cs", 305);
        WriteLines(dir.Root, "tests/View.axaml", 310);
        WriteLines(dir.Root, "tools/Ok.cs", 10);
        // bin/ and obj/ files over the limit must be excluded.
        WriteLines(dir.Root, "src/bin/Debug/Generated.cs", 999);
        WriteLines(dir.Root, "src/obj/Debug/More.cs", 999);
        // A .txt file (not .cs/.axaml) over the limit must be ignored.
        WriteLines(dir.Root, "src/Notes.txt", 999);

        var roots = new[] { "src", "tests", "tools" };
        var violations = FileSizeGuard.Enumerate(dir.Root, roots, 300);

        var paths = violations.Select(v => v.Path).OrderBy(p => p).ToArray();
        Assert.Equal(2, paths.Length);
        Assert.Contains(NormalizePath("src/Big.cs"), paths);
        Assert.Contains(NormalizePath("tests/View.axaml"), paths);
    }

    [Fact]
    public void Enumerate_GreenTree_ReturnsEmpty()
    {
        using var dir = new TempDir();
        WriteLines(dir.Root, "src/Small.cs", 50);
        WriteLines(dir.Root, "tests/Tiny.axaml", 5);

        var violations = FileSizeGuard.Enumerate(dir.Root, new[] { "src", "tests", "tools" }, 300);

        Assert.Empty(violations);
    }

    [Fact]
    public void Enumerate_MissingRoot_IsSkipped()
    {
        using var dir = new TempDir();
        WriteLines(dir.Root, "src/Ok.cs", 10);
        // "tools" and "tests" do not exist — must not throw.

        var violations = FileSizeGuard.Enumerate(dir.Root, new[] { "src", "tests", "tools" }, 300);

        Assert.Empty(violations);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string NormalizePath(string p) => p.Replace('/', Path.DirectorySeparatorChar);

    private static void WriteLines(string root, string relPath, int lineCount)
    {
        var full = Path.Combine(root, NormalizePath(relPath));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllLines(full, Enumerable.Range(1, lineCount).Select(i => $"line {i}"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "vr-filesize-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
