using VisualRelay.Core.Execution;
using Xunit;

namespace VisualRelay.Tests;

/// <summary>
/// FencedJsonExtractor must find the real contract block even when the JSON's
/// own string values quote fence markers — the exact shape that killed the
/// stage-contract-retry task twice (Diagnose output discussing ```json fences).
/// </summary>
public sealed class FencedJsonExtractorTests
{
    [Fact]
    public void SimpleFencedBlock_Extracts()
    {
        var text = "preamble\n```json\n{ \"summary\": \"ok\" }\n```\n";
        Assert.Equal("{ \"summary\": \"ok\" }", FencedJsonExtractor.Extract(text));
    }

    [Fact]
    public void ClosingFenceGluedToJsonLine_Extracts()
    {
        var text = "```json\n{ \"a\": 1 }```\n";
        Assert.Equal("{ \"a\": 1 }", FencedJsonExtractor.Extract(text));
    }

    [Fact]
    public void MarkerEmbeddedInStringValue_StillExtractsTheRealBlock()
    {
        // Mirrors the stage-contract-retry Diagnose output: the document's only
        // real fence opens at the top, but string values later contain the
        // literal text "```json" plus a quoted JSON fragment with escaped
        // quotes. LastIndexOf used to anchor on the embedded marker, brace-match
        // the quoted fragment, fail to parse, and return null.
        var json = "{ \"evidence\": \"the output lacks a fenced ```json block\\n" +
                   "and the runner returns `return new SubagentResult(...)`\", " +
                   "\"excerpt\": \"```json\\n{ \\\"summary\\\": \\\"quoted fragment\\\" }\\n```\" }";
        var text = "Diagnose findings follow.\n```json\n" + json + "\n```\n";

        Assert.Equal(json, FencedJsonExtractor.Extract(text));
    }

    [Fact]
    public void MultipleFencedBlocks_PrefersTheLastParseable()
    {
        var text = "```json\n{ \"draft\": true }\n```\nrevised:\n```json\n{ \"final\": true }\n```\n";
        Assert.Equal("{ \"final\": true }", FencedJsonExtractor.Extract(text));
    }

    [Fact]
    public void LastMarkerHasNoNewlineAfterIt_FallsBackToEarlierBlock()
    {
        var text = "```json\n{ \"real\": 1 }\n```\ntrailing mention of ```json";
        Assert.Equal("{ \"real\": 1 }", FencedJsonExtractor.Extract(text));
    }

    [Fact]
    public void LastBlockUnparseable_FallsBackToEarlierParseableBlock()
    {
        var text = "```json\n{ \"good\": 1 }\n```\n```json\n{ not valid json\n```\n";
        Assert.Equal("{ \"good\": 1 }", FencedJsonExtractor.Extract(text));
    }

    [Fact]
    public void NoFence_ReturnsNull()
    {
        Assert.Null(FencedJsonExtractor.Extract("just prose { \"a\": 1 }"));
    }

    [Fact]
    public void FenceWithNoJsonValue_ReturnsNull()
    {
        Assert.Null(FencedJsonExtractor.Extract("```json\nnothing here\n```"));
    }
}
