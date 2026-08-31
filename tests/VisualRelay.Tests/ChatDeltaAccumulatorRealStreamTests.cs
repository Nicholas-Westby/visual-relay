using System.Text.Json;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Drives the parser and the accumulator together over streams captured from the
/// four real providers, one byte at a time. This is the whole read path short of
/// the socket, so a provider changing its delta shape fails here rather than in
/// production.
/// </summary>
public sealed class ChatDeltaAccumulatorRealStreamTests
{
    private static ChatDeltaAccumulator Replay(string provider)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sse", $"{provider}-stream.sse");
        var parser = new SseFrameParser();
        var accumulator = new ChatDeltaAccumulator();

        void Fold(IReadOnlyList<SseFrame> frames)
        {
            foreach (var frame in frames.Where(f => f.Kind == SseFrameKind.Event))
            {
                using var doc = JsonDocument.Parse(frame.Data);
                accumulator.Append(doc.RootElement);
            }
        }

        foreach (var b in File.ReadAllBytes(path)) Fold(parser.Append([b]));
        Fold(parser.Flush());
        return accumulator;
    }

    /// <summary>
    /// Every captured stream yields measured usage and a finish reason, so a
    /// completed turn is never costed by estimate.
    /// </summary>
    /// <param name="provider">The captured provider fixture to replay.</param>
    [Theory]
    [InlineData("deepseek")]
    [InlineData("zai")]
    [InlineData("moonshot")]
    [InlineData("hf")]
    public void EveryCapturedStream_YieldsUsageAndAFinishReason(string provider)
    {
        var accumulator = Replay(provider);

        Assert.NotNull(accumulator.Usage);
        Assert.True(accumulator.Usage!.PromptTokens > 0);
        Assert.False(string.IsNullOrEmpty(accumulator.FinishReason));
        Assert.False(string.IsNullOrEmpty(accumulator.ServedModel));
    }

    /// <summary>
    /// The three non-reasoning captures produced ordinary content. Asserting the
    /// text arrives at all is what proves deltas are being folded, not dropped.
    /// </summary>
    /// <param name="provider">The captured provider fixture to replay.</param>
    [Theory]
    [InlineData("deepseek")]
    [InlineData("moonshot")]
    [InlineData("hf")]
    public void NonReasoningCaptures_ProduceContent(string provider)
    {
        var accumulator = Replay(provider);

        Assert.False(string.IsNullOrWhiteSpace(accumulator.Content()));
        Assert.True(accumulator.SawOutput);
    }

    /// <summary>
    /// The Z.AI capture is the hazard case: capped at 40 tokens it spent every
    /// one on reasoning, so it finished on "length" with reasoning text but no
    /// content at all. A caller must be able to tell this from an empty answer
    /// and retry with a larger budget rather than reporting nothing.
    /// </summary>
    [Fact]
    public void ZaiCapture_IsEmptyContentWithLengthFinishAndReasoning()
    {
        var accumulator = Replay("zai");

        Assert.Equal("length", accumulator.FinishReason);
        Assert.Equal(string.Empty, accumulator.Content());
        Assert.False(string.IsNullOrEmpty(accumulator.ReasoningContent()));
        Assert.True(accumulator.SawOutput);
        Assert.Equal(accumulator.Usage!.CompletionTokens, accumulator.Usage.ReasoningTokens);
    }
}
