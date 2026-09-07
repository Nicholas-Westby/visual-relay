using VisualRelay.Core.Agent;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the two ways a correct answer used to be thrown away: the scanner
/// losing its place on a quote written in prose, and a contract block whose only
/// defect is JSON punctuation the model got wrong. Every fixture here is a real
/// answer from a live run that was flagged, with the reasoning and the code both
/// correct.
/// </summary>
public sealed class StageContractRecoveryTests
{
    private const string SummaryContract =
        """End your reply with a single fenced json block, nothing after it, matching: { "summary": string, "options": string[] }""";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts", name));

    /// <summary>
    /// A single quote written in prose used to flip the scanner into string mode
    /// at depth 0, swallowing the opening brace of the real contract block. The
    /// stage answered correctly and was flagged for "no JSON object found".
    /// </summary>
    [Fact]
    public void AQuoteInProse_DoesNotHideTheContract()
    {
        var result = StageContractReader.Read(
            Fixture("quote-in-prose-answer.txt"),
            """matching: { "summary": string }""");

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("Fixed minify_string", result.Json!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same desync flagged a review stage, whose verdict was "changes" with a
    /// fully described issue. Nothing about it is language- or stage-specific.
    /// </summary>
    [Fact]
    public void AQuoteInProse_DoesNotHideAReviewVerdict()
    {
        var result = StageContractReader.Read(
            Fixture("review-quote-in-prose-answer.txt"),
            """matching: { "verdict": "pass"|"changes", "issues": [] }""");

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("\"verdict\"", result.Json!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Quotes outside an object are prose, not string delimiters. Only a scanner
    /// that starts tracking string state at the opening brace can say so.
    /// </summary>
    [Fact]
    public void QuotesAtDepthZero_AreIgnoredByTheScanner()
    {
        const string answer = """
            The guard compares against '"' and then against '\0', an odd pair.

            {"summary": "s", "options": []}
            """;

        var result = StageContractReader.Read(answer, SummaryContract);

        Assert.True(result.Succeeded, result.Error);
    }

    /// <summary>
    /// Multi-paragraph prose in a string value arrives with literal newlines. The
    /// answer is correct JSON in intent and one escape away from being valid.
    /// </summary>
    [Fact]
    public void RawControlCharacters_AreRepaired()
    {
        var result = StageContractReader.Read(
            Fixture("raw-control-characters-answer.txt"),
            """matching: { "findings": string, "constraints": string[] }""");

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("control characters", result.Repairs);
    }

    /// <summary>
    /// A regex quoted inside a string value carries <c>\d</c>, which JSON reads as
    /// an invalid escape. Doubling the backslash is the one repair that preserves
    /// what the model meant.
    /// </summary>
    [Fact]
    public void ARegexEscape_IsRepaired()
    {
        var result = StageContractReader.Read(Fixture("regex-escape-answer.txt"), SummaryContract);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("negativeNumberArg", result.Json!, StringComparison.Ordinal);
        Assert.Contains("invalid escapes", result.Repairs);
    }

    /// <summary>
    /// The same rule covers a backslash-backtick, which is what a model writes
    /// when it quotes a template literal inside its contract.
    /// </summary>
    [Fact]
    public void ABacktickEscape_IsRepaired()
    {
        var result = StageContractReader.Read(Fixture("backtick-escape-answer.txt"), SummaryContract);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("normalizeRetryOptions", result.Json!, StringComparison.Ordinal);
    }

    /// <summary>A trailing comma before a closing brace is dropped.</summary>
    [Fact]
    public void ATrailingComma_IsRepaired()
    {
        var result = StageContractReader.Read(
            """{ "summary": "s", "options": ["a",], }""", SummaryContract);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("trailing commas", result.Repairs);
    }

    /// <summary>
    /// Repair is a last resort. An answer that parses strictly is reported as
    /// unrepaired, so the warn event only ever fires on a real defect.
    /// </summary>
    [Fact]
    public void AValidContract_IsNotReportedAsRepaired()
    {
        var result = StageContractReader.Read(
            """{ "summary": "s", "options": [] }""", SummaryContract);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Repairs);
    }

    /// <summary>
    /// The repair rules are the three named ones and nothing else. An answer
    /// truncated mid-object is still refused, because guessing the rest of it
    /// would be inventing the model's work.
    /// </summary>
    [Fact]
    public void ATruncatedObject_IsStillRefused()
    {
        var result = StageContractReader.Read(
            """{ "summary": "s", "options": [ }""", SummaryContract);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }
}
