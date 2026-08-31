using System.Text.Json.Nodes;
using VisualRelay.Core.Agent;

namespace VisualRelay.Tests;

/// <summary>
/// Covers schema-aware argument repair: the two failures worth repairing are
/// JSON truncated mid-emission and a primitive sent as the wrong type. Anything
/// requiring a guess at intent is reported back to the model instead, which is
/// the line this class must not cross.
/// </summary>
public sealed class ToolArgumentRepairTests
{
    private static JsonNode Schema() => JsonNode.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "timeout_seconds": { "type": "number" },
            "recursive": { "type": "boolean" }
          },
          "required": ["path"]
        }
        """)!;

    /// <summary>Valid arguments pass through untouched, with no repair noted.</summary>
    [Fact]
    public void ValidArguments_AreUntouched()
    {
        var result = ToolArgumentRepair.Repair("""{"path":"src/Calc.py"}""", Schema());

        Assert.True(result.Succeeded);
        Assert.Null(result.Repair);
        Assert.Equal("src/Calc.py", result.Arguments!["path"]!.GetValue<string>());
    }

    /// <summary>Empty arguments read as an empty object rather than failing.</summary>
    [Fact]
    public void EmptyArguments_ReadAsAnEmptyObject()
    {
        var result = ToolArgumentRepair.Repair("", Schema());

        Assert.True(result.Succeeded);
        Assert.Empty(result.Arguments!.AsObject());
    }

    /// <summary>
    /// A model that stopped emitting mid-string gets the string closed and the
    /// object closed behind it, which is the commonest truncation shape.
    /// </summary>
    [Fact]
    public void TruncatedMidString_IsClosed()
    {
        var result = ToolArgumentRepair.Repair("""{"path":"src/Calc""", Schema());

        Assert.True(result.Succeeded);
        Assert.Equal("src/Calc", result.Arguments!["path"]!.GetValue<string>());
        Assert.Contains("truncated", result.Repair!, StringComparison.Ordinal);
    }

    /// <summary>Nested structures are closed in the order they were opened.</summary>
    [Fact]
    public void TruncatedNestedStructure_IsClosed()
    {
        // Ends mid-array, immediately after a closing quote.
        var result = ToolArgumentRepair.Repair("{\"command\":[\"sh\",\"run.sh\"", Schema());

        Assert.True(result.Succeeded);
        var command = result.Arguments!["command"]!.AsArray();
        Assert.Equal(2, command.Count);
        Assert.Equal("run.sh", command[1]!.GetValue<string>());
    }

    /// <summary>A dangling comma left by truncation is removed before closing.</summary>
    [Fact]
    public void TruncatedAfterAComma_IsClosed()
    {
        var result = ToolArgumentRepair.Repair("""{"path":"a",""", Schema());

        Assert.True(result.Succeeded);
        Assert.Equal("a", result.Arguments!["path"]!.GetValue<string>());
    }

    /// <summary>A number sent as a string is coerced, and the coercion is reported.</summary>
    [Fact]
    public void NumberSentAsAString_IsCoerced()
    {
        var result = ToolArgumentRepair.Repair(
            """{"path":"a","timeout_seconds":"600"}""", Schema());

        Assert.True(result.Succeeded);
        Assert.Equal(600, result.Arguments!["timeout_seconds"]!.GetValue<double>());
        Assert.Contains("timeout_seconds", result.Repair!, StringComparison.Ordinal);
    }

    /// <summary>A boolean sent as a string is coerced too.</summary>
    [Fact]
    public void BooleanSentAsAString_IsCoerced()
    {
        var result = ToolArgumentRepair.Repair(
            """{"path":"a","recursive":"true"}""", Schema());

        Assert.True(result.Succeeded);
        Assert.True(result.Arguments!["recursive"]!.GetValue<bool>());
    }

    /// <summary>A string that is not a number is left alone rather than mangled.</summary>
    [Fact]
    public void NonNumericString_IsNotCoerced()
    {
        var result = ToolArgumentRepair.Repair(
            """{"path":"a","timeout_seconds":"soon"}""", Schema());

        Assert.True(result.Succeeded);
        Assert.Equal("soon", result.Arguments!["timeout_seconds"]!.GetValue<string>());
        Assert.Null(result.Repair);
    }

    /// <summary>
    /// A missing required property is NOT invented. Repair fixes transport
    /// damage; supplying a value the model never sent would be a guess at intent.
    /// </summary>
    [Fact]
    public void MissingRequiredProperty_IsNotInvented()
    {
        var result = ToolArgumentRepair.Repair("""{"recursive":true}""", Schema());

        Assert.True(result.Succeeded);
        Assert.False(result.Arguments!.AsObject().ContainsKey("path"));
    }

    /// <summary>Arguments that are not an object at all are refused.</summary>
    [Fact]
    public void NonObjectArguments_AreRefused()
    {
        var result = ToolArgumentRepair.Repair("""["a","b"]""", Schema());

        Assert.False(result.Succeeded);
        Assert.Contains("JSON object", result.Repair!, StringComparison.Ordinal);
    }

    /// <summary>Text that is not JSON in any repairable sense is refused.</summary>
    [Fact]
    public void UnrepairableText_IsRefused()
    {
        var result = ToolArgumentRepair.Repair("I will read the file now", Schema());

        Assert.False(result.Succeeded);
        Assert.Contains("could not be repaired", result.Repair!, StringComparison.Ordinal);
    }

    /// <summary>Mismatched brackets are refused rather than force-closed.</summary>
    [Fact]
    public void MismatchedBrackets_AreRefused()
    {
        var result = ToolArgumentRepair.Repair("""{"path":"a"]""", Schema());

        Assert.False(result.Succeeded);
    }

    /// <summary>An escaped quote inside a string does not confuse the scanner.</summary>
    [Fact]
    public void EscapedQuotes_DoNotConfuseTheScanner()
    {
        var result = ToolArgumentRepair.Repair("""{"path":"a\"b"}""", Schema());

        Assert.True(result.Succeeded);
        Assert.Equal("""a"b""", result.Arguments!["path"]!.GetValue<string>());
    }

    /// <summary>Repair works with no schema, doing structural fixes only.</summary>
    [Fact]
    public void WithoutASchema_StructuralRepairStillWorks()
    {
        var result = ToolArgumentRepair.Repair("""{"path":"src/Calc""");

        Assert.True(result.Succeeded);
        Assert.Equal("src/Calc", result.Arguments!["path"]!.GetValue<string>());
    }
}
