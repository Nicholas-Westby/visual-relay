using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="NonoDiagnosticsJsonParser.TryExtractDenials"/> —
/// the tolerant JSON-tail parser that finds the last balanced <c>{…}</c>
/// containing <c>"denials"</c>, extracts the denial records, and strips the
/// JSON block from the captured output.  Absent, truncated, or malformed JSON
/// must never throw — the parser returns false with the output unchanged.
/// </summary>
public sealed class NonoDiagnosticsJsonParserTests
{
    // ── Happy path: valid trailing JSON with denials ──────────────────

    [Fact]
    public void TryExtractDenials_WithDenialsPresent_ExtractsAndStrips()
    {
        var json = "{\"denials\":[{\"operation\":\"file-write-create\",\"target\":\"/Volumes/Tera/.TemporaryItems/foo\"}]}";
        var output = "some command output\nmore output\n" + json;

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.True(result);
        Assert.NotNull(denials);
        Assert.Single(denials);
        Assert.Equal("file-write-create", denials[0].Operation);
        Assert.Equal("/Volumes/Tera/.TemporaryItems/foo", denials[0].Target);
        // JSON block must be removed from the output handed to callers.
        Assert.DoesNotContain("{", stripped);
        Assert.Equal("some command output\nmore output\n", stripped);
    }

    [Fact]
    public void TryExtractDenials_EmptyDenials_StillExtracts()
    {
        var output = "output line\n{\"denials\":[]}";

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.True(result);
        Assert.NotNull(denials);
        Assert.Empty(denials);
        Assert.Equal("output line\n", stripped);
    }

    [Fact]
    public void TryExtractDenials_MultipleDenials_ExtractsAll()
    {
        var json = "{\"denials\":[" +
            "{\"operation\":\"file-write-create\",\"target\":\"/tmp/a\"}," +
            "{\"operation\":\"file-read\",\"target\":\"/tmp/b\"}" +
            "]}";
        var output = "output\n" + json;

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.True(result);
        Assert.Equal(2, denials.Count);
        Assert.Equal("file-write-create", denials[0].Operation);
        Assert.Equal("/tmp/a", denials[0].Target);
        Assert.Equal("file-read", denials[1].Operation);
        Assert.Equal("/tmp/b", denials[1].Target);
        Assert.Equal("output\n", stripped);
    }

    // ── Absent / no denials key ───────────────────────────────────────

    [Fact]
    public void TryExtractDenials_NoJson_ReturnsFalse()
    {
        var output = "plain command output\nno json here";

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.False(result);
        Assert.Empty(denials);
        Assert.Equal(output, stripped);
    }

    [Fact]
    public void TryExtractDenials_JsonWithoutDenialsKey_ReturnsFalseAndDoesNotStrip()
    {
        // A trailing JSON object that does NOT contain "denials" is not
        // diagnostics — it may be legitimate command output.  Must not be stripped.
        var output = "output\n{\"other\":\"value\"}";

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.False(result);
        Assert.Empty(denials);
        Assert.Equal(output, stripped);
    }

    // ── Malformed / truncated ─────────────────────────────────────────

    [Fact]
    public void TryExtractDenials_MalformedJson_ReturnsFalse()
    {
        // Truncated — missing closing brace and bracket.
        var output = "output\n{\"denials\":[{\"operation\":\"file-write-create\"";

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.False(result);
        Assert.Empty(denials);
        Assert.Equal(output, stripped);
    }

    [Fact]
    public void TryExtractDenials_UnbalancedBraces_ReturnsFalse()
    {
        var output = "output\n{\"denials\":[{\"operation\":\"file-write-create\",\"target\":\"/tmp/foo\"}]";

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.False(result);
        Assert.Empty(denials);
        Assert.Equal(output, stripped);
    }

    // ── Edge cases ────────────────────────────────────────────────────

    [Fact]
    public void TryExtractDenials_JsonFollowedByTrailingContent_StripsJsonOnly()
    {
        // Diagnostics JSON is NOT the last thing in the output — there is
        // trailing content after it.  Only the JSON block itself is stripped.
        var output = "output\n{\"denials\":[{\"operation\":\"file-write-create\",\"target\":\"/tmp/foo\"}]}\ntrailing content\nmore trailing";

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.True(result);
        Assert.Single(denials);
        Assert.Equal("/tmp/foo", denials[0].Target);
        Assert.Equal("output\n\ntrailing content\nmore trailing", stripped);
    }

    [Fact]
    public void TryExtractDenials_MultipleJsonBlocks_PicksLastOneWithDenials()
    {
        // An earlier {…} block without "denials" is ignored; only the LAST
        // balanced block containing "denials" is extracted.
        var output = "{\"other\":1}\nreal output\n{\"denials\":[{\"operation\":\"file-write-create\",\"target\":\"/tmp/bar\"}]}";

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.True(result);
        Assert.Single(denials);
        Assert.Equal("/tmp/bar", denials[0].Target);
        Assert.DoesNotContain("denials", stripped);
    }

    [Fact]
    public void TryExtractDenials_EmptyOutput_ReturnsFalse()
    {
        var result = NonoDiagnosticsJsonParser.TryExtractDenials("", out var stripped, out var denials);

        Assert.False(result);
        Assert.Empty(denials);
        Assert.Equal("", stripped);
    }

    [Fact]
    public void TryExtractDenials_NullOutput_ReturnsFalse()
    {
        var result = NonoDiagnosticsJsonParser.TryExtractDenials(null!, out var stripped, out var denials);

        Assert.False(result);
        Assert.Empty(denials);
        Assert.Null(stripped);
    }

    [Fact]
    public void TryExtractDenials_JsonAppendedToLastOutputLine_StripsCleanly()
    {
        // JSON may be concatenated to the final line of output (no newline
        // separator).  The parser must still find and strip it.
        var output = "final line{\"denials\":[{\"operation\":\"file-write-create\",\"target\":\"/tmp/foo\"}]}";

        var result = NonoDiagnosticsJsonParser.TryExtractDenials(output, out var stripped, out var denials);

        Assert.True(result);
        Assert.Single(denials);
        Assert.Equal("/tmp/foo", denials[0].Target);
        Assert.Equal("final line", stripped);
    }
}
