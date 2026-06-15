using VisualRelay.Core.Execution;

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
    [Fact]
    public void EmbeddedMarkerFollowedByParseableArrayFragment_StillReturnsTheRealObjectBlock()
    {
        // The Plan-stage killer: the contract's string values embed "```json",
        // and the model recaps the manifest as a bare ARRAY in prose after the
        // closing fence. The walk anchors on the embedded marker, whose first
        // JSON value is that trailing array — parseable, so a parse-only walk
        // returns it and the driver throws (root must be Object). Object-root
        // acceptance rejects the fragment and falls back to the real block.
        var json = "{ \"plan\": \"emit a fenced ```json block listing files\", " +
                   "\"manifest\": [\"src/A.cs\", \"tests/B.cs\"] }";
        var text = "```json\n" + json + "\n```\nrecap of files:\n[\"src/A.cs\", \"tests/B.cs\"]\n";

        Assert.Equal(json, FencedJsonExtractor.Extract(text));
    }

    [Fact]
    public void OnlyBlockIsArrayRoot_ReturnsNull()
    {
        // An array-root contract is a contract violation; the extractor returns
        // null so the driver flags it cleanly instead of throwing on shape.
        Assert.Null(FencedJsonExtractor.Extract("```json\n[1, 2, 3]\n```"));
    }

}
