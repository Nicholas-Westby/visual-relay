using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class GitPathOutputTests
{
    // ── CUnquote: plain ASCII passes through ─────────────────────────

    [Fact]
    public void CUnquote_PlainAsciiPath_ReturnsUnchanged()
    {
        var result = GitPathOutput.CUnquote("src/foo.cs");
        Assert.Equal("src/foo.cs", result);
    }

    [Fact]
    public void CUnquote_EmptyString_ReturnsEmpty()
    {
        var result = GitPathOutput.CUnquote("");
        Assert.Equal("", result);
    }

    // ── CUnquote: surrounding quotes stripped ────────────────────────

    [Fact]
    public void CUnquote_QuotedAscii_StripsSurroundingQuotes()
    {
        var result = GitPathOutput.CUnquote("\"hello.txt\"");
        Assert.Equal("hello.txt", result);
    }

    [Fact]
    public void CUnquote_QuotedWithSpaces_StripsQuotesAndPreservesContent()
    {
        var result = GitPathOutput.CUnquote("\"file with spaces.log\"");
        Assert.Equal("file with spaces.log", result);
    }

    // ── CUnquote: backslash escape sequences ─────────────────────────

    [Fact]
    public void CUnquote_EscapedQuote_DecodesToQuote()
    {
        var result = GitPathOutput.CUnquote("\"a\\\"b\"");
        Assert.Equal("a\"b", result);
    }

    [Fact]
    public void CUnquote_EscapedBackslash_DecodesToBackslash()
    {
        var result = GitPathOutput.CUnquote("\"a\\\\b\"");
        Assert.Equal("a\\b", result);
    }

    [Fact]
    public void CUnquote_EscapedTab_DecodesToTab()
    {
        var result = GitPathOutput.CUnquote("\"a\\tb\"");
        Assert.Equal("a\tb", result);
    }

    [Fact]
    public void CUnquote_EscapedNewline_DecodesToNewline()
    {
        var result = GitPathOutput.CUnquote("\"a\\nb\"");
        Assert.Equal("a\nb", result);
    }

    [Fact]
    public void CUnquote_EscapedCarriageReturn_DecodesToCarriageReturn()
    {
        var result = GitPathOutput.CUnquote("\"a\\rb\"");
        Assert.Equal("a\rb", result);
    }

    // ── CUnquote: octal escapes (the core bug fix) ───────────────────

    /// <summary>
    /// U+202F NARROW NO-BREAK SPACE encodes as UTF-8 bytes 0xE2 0x80 0xAF,
    /// which git emits as \342\200\257.
    /// </summary>
    [Fact]
    public void CUnquote_OctalEscapesForNarrowNoBreakSpace_DecodesToChar()
    {
        // "hello\342\200\257world" — the triple-octal sequence for U+202F
        var input = "\"hello\\342\\200\\257world\"";
        var result = GitPathOutput.CUnquote(input);
        Assert.Equal("hello\u202Fworld", result);
    }

    [Fact]
    public void CUnquote_LeadingOctalSequence_DecodesCorrectly()
    {
        // "\342\200\257PM.png" — U+202F right before "PM"
        var input = "\"\\342\\200\\257PM.png\"";
        var result = GitPathOutput.CUnquote(input);
        Assert.Equal("\u202FPM.png", result);
    }

    [Fact]
    public void CUnquote_SingleOctalByte_DecodesCorrectly()
    {
        // \041 = 0x21 = '!' (single octal for printable ASCII)
        var input = "\"got\\041it\"";
        var result = GitPathOutput.CUnquote(input);
        Assert.Equal("got!it", result);
    }

    [Fact]
    public void CUnquote_TwoDigitOctal_DecodesCorrectly()
    {
        // \134 = 0x5C = '\' (two-digit octal)
        var input = "\"hi\\134lo\"";
        var result = GitPathOutput.CUnquote(input);
        Assert.Equal("hi\\lo", result);
    }

    [Fact]
    public void CUnquote_MultipleOctalSequencesInOnePath_DecodesAll()
    {
        // Simulates a CJK character: 0xE6 0x97 0xA5 (日) = \346\227\245
        var input = "\"file-\\346\\227\\245.txt\"";
        var result = GitPathOutput.CUnquote(input);
        Assert.Equal("file-\u65E5.txt", result);
    }

    [Fact]
    public void CUnquote_RealScreenshotFilename_DecodesCorrectly()
    {
        // Exact reproduction from the 2026-07-15 drain log
        var input = "\"Screenshot 2026-07-15 at 9.37.05\\342\\200\\257PM.png\"";
        var result = GitPathOutput.CUnquote(input);
        Assert.Equal("Screenshot 2026-07-15 at 9.37.05\u202FPM.png", result);
    }

    // ── ParseLines: integration ──────────────────────────────────────

    [Fact]
    public void ParseLines_EmptyOutput_ReturnsEmptyList()
    {
        var result = GitPathOutput.ParseLines("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseLines_WhitespaceOnly_ReturnsEmptyList()
    {
        var result = GitPathOutput.ParseLines("  \n  \r\n  ");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseLines_MultiplePlainPaths_ReturnsAll()
    {
        var result = GitPathOutput.ParseLines("src/foo.cs\nlib/bar.js\ndocs/readme.md\n");
        Assert.Equal(["src/foo.cs", "lib/bar.js", "docs/readme.md"], result);
    }

    [Fact]
    public void ParseLines_MixedQuotedAndUnquoted_DecodesOnlyQuoted()
    {
        var output = "\"hello\\342\\200\\257world.txt\"\nplain-file.cs\n\"quoted\\tpath.log\"\n";
        var result = GitPathOutput.ParseLines(output);
        Assert.Equal(["hello\u202Fworld.txt", "plain-file.cs", "quoted\tpath.log"], result);
    }

    [Fact]
    public void ParseLines_TrailingBlankLines_Skipped()
    {
        var result = GitPathOutput.ParseLines("a.txt\n\nb.txt\n\n");
        Assert.Equal(["a.txt", "b.txt"], result);
    }

    [Fact]
    public void ParseLines_NullOutput_ReturnsEmptyList()
    {
        var result = GitPathOutput.ParseLines(null!);
        Assert.Empty(result);
    }
}
