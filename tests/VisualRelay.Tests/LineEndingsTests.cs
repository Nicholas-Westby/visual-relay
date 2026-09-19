using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class LineEndingsTests
{
    [Theory]
    [InlineData(null, "a\r\nb\n", "a\nb\n")]
    [InlineData("x\ny\n", "a\r\nb\r\n", "a\nb\n")]
    [InlineData("x\r\ny\r\n", "a\nb\n", "a\r\nb\r\n")]
    // Text that is already mixed ends up in one set of endings, with no doubled CR.
    [InlineData("x\r\n", "a\r\nb\nc", "a\r\nb\r\nc")]
    // Mostly CRLF was checked out CRLF; one stray LF line does not change that.
    [InlineData("x\r\ny\nz\r\n", "a\nb\n", "a\r\nb\r\n")]
    // And the reverse: one CRLF that a stray CR left in an LF file does not flip it.
    [InlineData("x\ny\nz\r\n", "a\nb\n", "a\nb\n")]
    public void Match_UsesTheEndingsTheExistingTextHas(string? existing, string text, string expected)
    {
        Assert.Equal(expected, LineEndings.Match(existing, text));
    }

    /// <summary>
    /// Only CRLF and LF are line breaks here. A lone CR is part of the text, and
    /// rewriting it would change content, not endings.
    /// </summary>
    [Theory]
    [InlineData(null, "a\rb\n", "a\rb\n")]
    [InlineData("x\r\n", "a\rb\n", "a\rb\r\n")]
    public void Match_LeavesALoneCarriageReturnAlone(string? existing, string text, string expected)
    {
        Assert.Equal(expected, LineEndings.Match(existing, text));
    }

    [Fact]
    public void ForFile_AFileThatDoesNotExistYet_IsLf()
    {
        var path = Path.Combine(Path.GetTempPath(), "vr-line-endings", Guid.NewGuid().ToString("N"), "absent.md");

        Assert.Equal("a\nb\n", LineEndings.ForFile(path, "a\r\nb\r\n"));
    }

    [Fact]
    public void ForFile_ReadsTheEndingsOfTheFileOnDisk()
    {
        using var repo = TestRepository.Create();
        var path = Path.Combine(repo.Root, "checked-out.md");
        File.WriteAllText(path, "x\r\ny\r\n");

        Assert.Equal("a\r\nb\r\n", LineEndings.ForFile(path, "a\nb\n"));
    }

    /// <summary>
    /// VERSION is written LF outright rather than matched: this repository says
    /// <c>eol=lf</c>, and the pre-commit hook rewrites it on every commit, so a CRLF
    /// write left it modified after every Windows commit. The raw bytes are read, not
    /// the trimmed text, and only a Windows run can see this regress, since the
    /// platform newline is already LF everywhere else.
    /// </summary>
    [Fact]
    public void TheVersionFile_IsWrittenLfWhateverThePlatform()
    {
        using var repo = TestRepository.Create();
        var path = Path.Combine(repo.Root, "VERSION");
        File.WriteAllText(path, "0.7\n");

        VersionHelper.BumpVersionFile(path);

        Assert.Equal("0.8\n", File.ReadAllText(path));
    }
}
