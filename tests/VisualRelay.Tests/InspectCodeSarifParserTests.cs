using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="InspectCodeSarifParser.CountResults"/> — the
/// <c>System.Text.Json</c> replacement for the inline <c>python3</c> in
/// <c>tools/guards/inspect-code.sh</c>. It counts <c>runs[].results[]</c> across
/// all runs; the gate fails when the count is non-zero (carve-outs already
/// removed from the SARIF via <c>.editorconfig</c>). Written TDD.
/// </summary>
public sealed class InspectCodeSarifParserTests
{
    [Fact]
    public void EmptyResults_ReturnsZero()
    {
        const string sarif = """
        { "runs": [ { "results": [] } ] }
        """;

        Assert.Equal(0, InspectCodeSarifParser.CountResults(sarif));
    }

    [Fact]
    public void NoRuns_ReturnsZero()
    {
        const string sarif = """{ "version": "2.1.0" }""";

        Assert.Equal(0, InspectCodeSarifParser.CountResults(sarif));
    }

    [Fact]
    public void RunWithoutResultsArray_ReturnsZero()
    {
        const string sarif = """{ "runs": [ { "tool": {} } ] }""";

        Assert.Equal(0, InspectCodeSarifParser.CountResults(sarif));
    }

    [Fact]
    public void SingleResult_ReturnsOne()
    {
        const string sarif = """
        { "runs": [ { "results": [ { "ruleId": "X" } ] } ] }
        """;

        Assert.Equal(1, InspectCodeSarifParser.CountResults(sarif));
    }

    [Fact]
    public void ResultsAcrossMultipleRuns_AreSummed()
    {
        const string sarif = """
        {
          "runs": [
            { "results": [ { "ruleId": "A" }, { "ruleId": "B" } ] },
            { "results": [ { "ruleId": "C" } ] }
          ]
        }
        """;

        Assert.Equal(3, InspectCodeSarifParser.CountResults(sarif));
    }
}
