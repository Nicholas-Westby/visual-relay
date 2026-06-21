using VisualRelay.App.Services;

namespace VisualRelay.Tests;

public sealed class OutputFieldParserTests
{
    [Fact]
    public void Parse_NullInput_ReturnsEmpty()
    {
        var result = OutputFieldParser.Parse(null);
        Assert.Empty(result.Fields);
        Assert.Equal("", result.RawJson);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        var result = OutputFieldParser.Parse("");
        Assert.Empty(result.Fields);
        Assert.Equal("", result.RawJson);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsSingleTextField()
    {
        var result = OutputFieldParser.Parse("   \n  ");
        Assert.Single(result.Fields);
        Assert.Equal("Output", result.Fields[0].Label);
        Assert.Equal(OutputFieldKind.Text, result.Fields[0].Kind);
        Assert.Equal("   \n  ", result.Fields[0].Value);
        Assert.Equal("   \n  ", result.RawJson);
    }

    [Fact]
    public void Parse_PlanContract_StringAndListFields()
    {
        var json = """{"plan": "Implement feature X", "manifest": ["file1.cs", "file2.cs"]}""";

        var result = OutputFieldParser.Parse(json);

        Assert.Equal(2, result.Fields.Count);

        Assert.Equal("plan", result.Fields[0].Label);
        Assert.Equal(OutputFieldKind.Text, result.Fields[0].Kind);
        Assert.Equal("Implement feature X", result.Fields[0].Value);

        Assert.Equal("manifest", result.Fields[1].Label);
        Assert.Equal(OutputFieldKind.List, result.Fields[1].Kind);
        Assert.Equal("file1.cs\nfile2.cs", result.Fields[1].Value);

        // RawJson should be pretty-printed
        Assert.Contains("\"plan\"", result.RawJson);
        Assert.Contains("\"manifest\"", result.RawJson);
    }

    [Fact]
    public void Parse_ReviewContract_TextAndEmptyList()
    {
        var json = """{"verdict": "pass", "issues": []}""";

        var result = OutputFieldParser.Parse(json);

        Assert.Equal(2, result.Fields.Count);

        Assert.Equal("verdict", result.Fields[0].Label);
        Assert.Equal(OutputFieldKind.Text, result.Fields[0].Kind);
        Assert.Equal("pass", result.Fields[0].Value);

        Assert.Equal("issues", result.Fields[1].Label);
        Assert.Equal(OutputFieldKind.List, result.Fields[1].Kind);
        Assert.Equal("", result.Fields[1].Value);
    }

    [Fact]
    public void Parse_NestedObject_BecomesJsonField()
    {
        var json = """{"summary": "done", "metadata": {"author": "bot", "version": 2}}""";

        var result = OutputFieldParser.Parse(json);

        Assert.Equal(2, result.Fields.Count);

        Assert.Equal("summary", result.Fields[0].Label);
        Assert.Equal(OutputFieldKind.Text, result.Fields[0].Kind);

        Assert.Equal("metadata", result.Fields[1].Label);
        Assert.Equal(OutputFieldKind.Json, result.Fields[1].Kind);
        Assert.Contains("\"author\"", result.Fields[1].Value);
        Assert.Contains("\"version\"", result.Fields[1].Value);
    }

    [Fact]
    public void Parse_NonJsonBlob_ReturnsSingleTextField()
    {
        var blob = "This is not JSON at all.\nJust some text output.";

        var result = OutputFieldParser.Parse(blob);

        Assert.Single(result.Fields);
        Assert.Equal("Output", result.Fields[0].Label);
        Assert.Equal(OutputFieldKind.Text, result.Fields[0].Kind);
        Assert.Equal(blob, result.Fields[0].Value);
        Assert.Equal(blob, result.RawJson);
    }

    [Fact]
    public void Parse_FencedJsonBlock_ExtractsJson()
    {
        var output = """
            Some preamble text before the JSON block.
            ```json
            {"plan": "Do the thing", "manifest": ["a.cs", "b.cs"]}
            ```
            Some trailing text after.
            """;

        var result = OutputFieldParser.Parse(output);

        Assert.Equal(2, result.Fields.Count);
        Assert.Equal("plan", result.Fields[0].Label);
        Assert.Equal(OutputFieldKind.Text, result.Fields[0].Kind);
        Assert.Equal("Do the thing", result.Fields[0].Value);

        Assert.Equal("manifest", result.Fields[1].Label);
        Assert.Equal(OutputFieldKind.List, result.Fields[1].Kind);
        Assert.Equal("a.cs\nb.cs", result.Fields[1].Value);

        // RawJson should be the pretty-printed extracted JSON
        Assert.Contains("\"plan\"", result.RawJson);
        Assert.DoesNotContain("preamble", result.RawJson);
    }

    [Fact]
    public void Parse_FencedJsonBlock_MultipleFences_UsesLastParseable()
    {
        // Simulates model output with embedded ```json in a string value,
        // where the real contract is the last parseable JSON block.
        var output = """
            ```json
            {"plan": "wrong", "manifest": ["x.cs"]}
            ```
            Actually, let me fix that:
            ```json
            {"plan": "correct", "manifest": ["a.cs", "b.cs", "c.cs"]}
            ```
            """;

        var result = OutputFieldParser.Parse(output);

        Assert.Equal(2, result.Fields.Count);
        Assert.Equal("plan", result.Fields[0].Label);
        Assert.Equal("correct", result.Fields[0].Value);
    }

    [Fact]
    public void Parse_NumberField_BecomesJsonField()
    {
        var json = """{"count": 42, "name": "test"}""";

        var result = OutputFieldParser.Parse(json);

        Assert.Equal(2, result.Fields.Count);

        Assert.Equal("count", result.Fields[0].Label);
        Assert.Equal(OutputFieldKind.Json, result.Fields[0].Kind);
        Assert.Equal("42", result.Fields[0].Value.Trim());

        Assert.Equal("name", result.Fields[1].Label);
        Assert.Equal(OutputFieldKind.Text, result.Fields[1].Kind);
    }

    [Fact]
    public void Parse_BooleanField_BecomesJsonField()
    {
        var json = """{"success": true, "message": "done"}""";

        var result = OutputFieldParser.Parse(json);

        Assert.Equal(2, result.Fields.Count);

        Assert.Equal("success", result.Fields[0].Label);
        Assert.Equal(OutputFieldKind.Json, result.Fields[0].Kind);

        Assert.Equal("message", result.Fields[1].Label);
        Assert.Equal(OutputFieldKind.Text, result.Fields[1].Kind);
    }

    [Fact]
    public void Parse_ArrayOfNonStrings_BecomesJsonField()
    {
        var json = """{"items": [1, 2, 3], "name": "list"}""";

        var result = OutputFieldParser.Parse(json);

        Assert.Equal(2, result.Fields.Count);

        Assert.Equal("items", result.Fields[0].Label);
        // Array of numbers → Json (not List, since List requires string elements)
        Assert.Equal(OutputFieldKind.Json, result.Fields[0].Kind);

        Assert.Equal("name", result.Fields[1].Label);
        Assert.Equal(OutputFieldKind.Text, result.Fields[1].Kind);
    }

    [Fact]
    public void Parse_RawJson_PrettyPrinted()
    {
        var json = """{"a":1,"b":"two"}""";

        var result = OutputFieldParser.Parse(json);

        Assert.Contains("""
            "a": 1
            """.Trim(), result.RawJson);
        Assert.Contains("""
            "b": "two"
            """.Trim(), result.RawJson);
    }
}
