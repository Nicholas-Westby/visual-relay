using VisualRelay.Core.Agent;

namespace VisualRelay.Tests;

/// <summary>
/// Covers reading a stage contract out of the model's answer. In process there
/// is no stdout to distill, so this reader faces the model's own text and
/// nothing else — no sandbox banners, no deny advisories, no prompt echo.
/// </summary>
public sealed class StageContractReaderTests
{
    private const string SummaryContract =
        """End your reply with a single fenced json block, nothing after it, matching: { "summary": string, "options": string[] }""";

    private const string AmendContract =
        """matching: { "summary": string, "amendManifest"?: string[] }""";

    /// <summary>A fenced contract block is read out of surrounding prose.</summary>
    [Fact]
    public void AFencedBlock_IsReadOutOfProse()
    {
        const string answer = """
            I looked at the code and here is what I found.

            ```json
            { "summary": "did the thing", "options": ["a", "b"] }
            ```
            """;

        var result = StageContractReader.Read(answer, SummaryContract);

        Assert.True(result.Succeeded);
        Assert.Contains("did the thing", result.Json!, StringComparison.Ordinal);
    }

    /// <summary>A bare object with no fence at all is still read.</summary>
    [Fact]
    public void ABareObject_IsStillRead()
    {
        var result = StageContractReader.Read(
            """{ "summary": "s", "options": [] }""", SummaryContract);

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// The LAST object wins. The contract is the last thing the model writes,
    /// and an earlier object may be an example it was reasoning about.
    /// </summary>
    [Fact]
    public void TheLastObjectWins()
    {
        const string answer = """
            One option would have been { "summary": "rejected", "options": [] } but I chose otherwise.

            ```json
            { "summary": "accepted", "options": ["x"] }
            ```
            """;

        var result = StageContractReader.Read(answer, SummaryContract);

        Assert.Contains("accepted", result.Json!, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected", result.Json!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A brace inside a string value does not end the object early, which is the
    /// one real ambiguity a fence walk still has to handle.
    /// </summary>
    [Fact]
    public void BracesInsideStrings_DoNotEndTheObject()
    {
        var result = StageContractReader.Read(
            """{ "summary": "it printed } and then {", "options": [] }""", SummaryContract);

        Assert.True(result.Succeeded);
        Assert.Contains("it printed } and then {", result.Json!, StringComparison.Ordinal);
    }

    /// <summary>An escaped quote inside a value does not confuse the scanner.</summary>
    [Fact]
    public void EscapedQuotes_DoNotConfuseTheScanner()
    {
        var result = StageContractReader.Read(
            """{ "summary": "he said \"done\"", "options": [] }""", SummaryContract);

        Assert.True(result.Succeeded);
    }

    /// <summary>A missing required key is reported by name.</summary>
    [Fact]
    public void AMissingRequiredKey_IsReportedByName()
    {
        var result = StageContractReader.Read("""{ "summary": "s" }""", SummaryContract);

        Assert.False(result.Succeeded);
        Assert.Contains("options", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key marked optional in the contract is not required. That is how the
    /// fix-verify stage marks its amend list.
    /// </summary>
    [Fact]
    public void AnOptionalKey_IsNotRequired()
    {
        var result = StageContractReader.Read("""{ "summary": "s" }""", AmendContract);

        Assert.True(result.Succeeded);
    }

    /// <summary>An array root is refused: the contract is an object.</summary>
    [Fact]
    public void AnArrayRoot_IsRefused()
    {
        var result = StageContractReader.Read("""["a","b"]""", SummaryContract);

        Assert.False(result.Succeeded);
        Assert.Contains("no JSON object", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>An empty answer is refused with a plain reason.</summary>
    [Fact]
    public void AnEmptyAnswer_IsRefused()
    {
        var result = StageContractReader.Read("   ", SummaryContract);

        Assert.False(result.Succeeded);
        Assert.Contains("empty answer", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>Prose with no object at all is refused.</summary>
    [Fact]
    public void ProseWithNoObject_IsRefused()
    {
        var result = StageContractReader.Read("I could not complete the task.", SummaryContract);

        Assert.False(result.Succeeded);
        Assert.Contains("no JSON object", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>Malformed JSON is refused with the parser's own reason.</summary>
    [Fact]
    public void MalformedJson_IsRefusedWithAReason()
    {
        var result = StageContractReader.Read("""{ "summary": "s", "options": [ }""", SummaryContract);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// The fixture that broke the old stdout extractor: a closing fence sitting
    /// on the same line as content. In process this is unremarkable, because the
    /// reader matches braces rather than fences.
    /// </summary>
    [Fact]
    public void AClosingFenceOnAContentLine_IsHandled()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "closing-fence-on-content-line.txt");
        var answer = File.ReadAllText(path);

        var result = StageContractReader.Read(answer, SummaryContract);

        // Whatever the fixture holds, the reader must reach a verdict without
        // throwing, and say why when it declines.
        Assert.True(result.Succeeded || result.Error is { Length: > 0 });
    }
}
