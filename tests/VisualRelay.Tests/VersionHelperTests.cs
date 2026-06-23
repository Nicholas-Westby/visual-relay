using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Pure unit tests for <see cref="VersionHelper"/> — the C# heart of the
/// auto-incrementing 0.x version. Covers bump arithmetic, parsing, formatting,
/// file-based bump (with temp files), and the assembly-version reader.
/// The hook (<c>.githooks/pre-commit</c>) delegates to
/// <see cref="VersionHelper.BumpVersionFile"/>; every other consumer reads the
/// baked-in assembly version via <see cref="VersionHelper.ReadInformationalVersion"/>.
/// </summary>
public sealed class VersionHelperTests
{
    // ── Bump ──────────────────────────────────────────────────────────────

    [Fact]
    public void Bump_0_1_Returns_0_2()
    {
        Assert.Equal("0.2", VersionHelper.Bump("0.1"));
    }

    [Fact]
    public void Bump_0_9_Returns_0_10()
    {
        Assert.Equal("0.10", VersionHelper.Bump("0.9"));
    }

    [Fact]
    public void Bump_0_99_Returns_0_100()
    {
        Assert.Equal("0.100", VersionHelper.Bump("0.99"));
    }

    [Fact]
    public void Bump_0_0_Returns_0_1()
    {
        Assert.Equal("0.1", VersionHelper.Bump("0.0"));
    }

    [Fact]
    public void Bump_0_42_Returns_0_43()
    {
        Assert.Equal("0.43", VersionHelper.Bump("0.42"));
    }

    [Fact]
    public void Bump_InvalidInput_Throws()
    {
        Assert.ThrowsAny<Exception>(() => VersionHelper.Bump("1.0"));
        Assert.ThrowsAny<Exception>(() => VersionHelper.Bump("garbage"));
        Assert.ThrowsAny<Exception>(() => VersionHelper.Bump(""));
    }

    // ── TryParse ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.1", 1)]
    [InlineData("0.9", 9)]
    [InlineData("0.10", 10)]
    [InlineData("0.99", 99)]
    [InlineData("0.0", 0)]
    [InlineData("0.42", 42)]
    public void TryParse_Valid_ReturnsTrue(string text, int expectedMinor)
    {
        Assert.True(VersionHelper.TryParse(text, out var minor));
        Assert.Equal(expectedMinor, minor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("1.0")]
    [InlineData("2.5")]
    [InlineData("0.1.2")]
    [InlineData("v0.1")]
    [InlineData("0.1-beta")]
    [InlineData("-0.1")]
    [InlineData("0.-1")]
    [InlineData("0.1.0")]
    public void TryParse_Invalid_ReturnsFalse(string? text)
    {
        Assert.False(VersionHelper.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_WhitespaceAroundValid_ReturnsFalse()
    {
        // The VERSION file is trimmed by BumpVersionFile, but TryParse itself
        // is strict: no leading/trailing whitespace.
        Assert.False(VersionHelper.TryParse(" 0.1", out _));
        Assert.False(VersionHelper.TryParse("0.1 ", out _));
        Assert.False(VersionHelper.TryParse(" 0.1 ", out _));
    }

    [Fact]
    public void TryParse_LeadingZerosInMinor_Accepted()
    {
        // "0.01" → minor 1 (int.Parse handles leading zeros naturally)
        Assert.True(VersionHelper.TryParse("0.01", out var minor));
        Assert.Equal(1, minor);
    }

    // ── Format ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, "0.1")]
    [InlineData(9, "0.9")]
    [InlineData(10, "0.10")]
    [InlineData(42, "0.42")]
    [InlineData(100, "0.100")]
    [InlineData(0, "0.0")]
    public void Format_ReturnsExpected(int minor, string expected)
    {
        Assert.Equal(expected, VersionHelper.Format(minor));
    }

    // ── BumpVersionFile ───────────────────────────────────────────────────

    [Fact]
    public void BumpVersionFile_Normal_ReadsBumpsWrites()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "VERSION");
        File.WriteAllText(path, "0.7");

        var result = VersionHelper.BumpVersionFile(path);

        Assert.Equal("0.8", result);
        Assert.Equal("0.8", File.ReadAllText(path).Trim());
    }

    [Fact]
    public void BumpVersionFile_MissingFile_SeedsWith_0_1()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "VERSION");

        var result = VersionHelper.BumpVersionFile(path);

        Assert.Equal("0.1", result);
        Assert.True(File.Exists(path));
        Assert.Equal("0.1", File.ReadAllText(path).Trim());
    }

    [Fact]
    public void BumpVersionFile_EmptyFile_SeedsWith_0_1()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "VERSION");
        File.WriteAllText(path, "");

        var result = VersionHelper.BumpVersionFile(path);

        Assert.Equal("0.1", result);
        Assert.Equal("0.1", File.ReadAllText(path).Trim());
    }

    [Fact]
    public void BumpVersionFile_WhitespaceOnlyFile_SeedsWith_0_1()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "VERSION");
        File.WriteAllText(path, "   \n  \n  ");

        var result = VersionHelper.BumpVersionFile(path);

        Assert.Equal("0.1", result);
        Assert.Equal("0.1", File.ReadAllText(path).Trim());
    }

    [Fact]
    public void BumpVersionFile_GarbledFile_SeedsWith_0_1()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "VERSION");
        File.WriteAllText(path, "not-a-version");

        var result = VersionHelper.BumpVersionFile(path);

        Assert.Equal("0.1", result);
        Assert.Equal("0.1", File.ReadAllText(path).Trim());
    }

    [Fact]
    public void BumpVersionFile_FileWithLeadingTrailingWhitespace_BumpsCorrectly()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "VERSION");
        File.WriteAllText(path, "  0.7\n");

        var result = VersionHelper.BumpVersionFile(path);

        Assert.Equal("0.8", result);
        Assert.Equal("0.8", File.ReadAllText(path).Trim());
    }

    [Fact]
    public void BumpVersionFile_ConsecutiveBumps_IncrementCorrectly()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "VERSION");
        File.WriteAllText(path, "0.1");

        var r1 = VersionHelper.BumpVersionFile(path);
        var r2 = VersionHelper.BumpVersionFile(path);
        var r3 = VersionHelper.BumpVersionFile(path);

        Assert.Equal("0.2", r1);
        Assert.Equal("0.3", r2);
        Assert.Equal("0.4", r3);
        Assert.Equal("0.4", File.ReadAllText(path).Trim());
    }

    // ── ReadInformationalVersion ──────────────────────────────────────────

    [Fact]
    public void ReadInformationalVersion_ReturnsNonEmptyString()
    {
        var version = VersionHelper.ReadInformationalVersion();

        Assert.False(string.IsNullOrWhiteSpace(version),
            "ReadInformationalVersion should return a non-empty version string");
    }

    [Fact]
    public void ReadInformationalVersion_IsNotSdkDefault()
    {
        // Before the VERSION file is wired into the build, the SDK emits
        // "1.0.0+<hash>". The real build must replace that default.
        var version = VersionHelper.ReadInformationalVersion();

        // The default 1.0.0 prefix signals the build wiring hasn't landed.
        Assert.DoesNotContain("1.0.0", version, StringComparison.Ordinal);
    }

    // ── Round-trip ────────────────────────────────────────────────────────

    [Fact]
    public void BumpAndParse_RoundTrip()
    {
        var bumped = VersionHelper.Bump("0.5");
        Assert.True(VersionHelper.TryParse(bumped, out var minor));
        Assert.Equal(6, minor);
    }

    [Fact]
    public void FormatAndParse_RoundTrip()
    {
        var formatted = VersionHelper.Format(37);
        Assert.True(VersionHelper.TryParse(formatted, out var minor));
        Assert.Equal(37, minor);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>A temporary directory cleaned up on dispose.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "vr-version-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Swallow — leaking a temp dir is acceptable.
            }
        }
    }
}
