using System.Text.Json;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Runs <see cref="SseFrameParser"/> over streams captured from the four real
/// providers on 2026-08-31, fed a byte at a time so every chunk boundary in the
/// payload is exercised. Synthetic frames prove the grammar; these prove the
/// grammar the providers actually speak, including where each one puts
/// <c>usage</c>.
/// </summary>
public sealed class SseFrameParserRealStreamTests
{
    private static IReadOnlyList<SseFrame> ParseByteAtATime(string provider)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sse", $"{provider}-stream.sse");
        var bytes = File.ReadAllBytes(path);
        var parser = new SseFrameParser();
        var frames = new List<SseFrame>();
        foreach (var b in bytes) frames.AddRange(parser.Append([b]));
        frames.AddRange(parser.Flush());
        Assert.True(parser.SawDone, $"{provider} stream did not carry its [DONE] terminator");
        return frames;
    }

    /// <summary>Reads the usage object from wherever a provider chose to put it.</summary>
    private static JsonElement? UsageIn(SseFrame frame)
    {
        if (frame.Kind != SseFrameKind.Event) return null;
        using var doc = JsonDocument.Parse(frame.Data);
        var root = doc.RootElement;
        if (root.TryGetProperty("usage", out var top) && top.ValueKind == JsonValueKind.Object)
            return top.Clone();
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("usage", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
            return nested.Clone();
        return null;
    }

    /// <summary>
    /// Every captured stream parses end to end into events plus a terminator,
    /// with no frame failing to be JSON.
    /// </summary>
    /// <param name="provider">The captured provider fixture to parse.</param>
    [Theory]
    [InlineData("deepseek")]
    [InlineData("zai")]
    [InlineData("moonshot")]
    [InlineData("hf")]
    public void CapturedStream_ParsesEveryEventAsJson(string provider)
    {
        var frames = ParseByteAtATime(provider);

        Assert.Contains(frames, f => f.Kind == SseFrameKind.Done);
        var events = frames.Where(f => f.Kind == SseFrameKind.Event).ToList();
        Assert.NotEmpty(events);
        foreach (var e in events)
        {
            using var doc = JsonDocument.Parse(e.Data);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
    }

    /// <summary>
    /// Every provider reports usage somewhere, and it is always reachable from
    /// the union of the two places any of them use.
    /// </summary>
    /// <param name="provider">The captured provider fixture to parse.</param>
    [Theory]
    [InlineData("deepseek")]
    [InlineData("zai")]
    [InlineData("moonshot")]
    [InlineData("hf")]
    public void CapturedStream_CarriesUsageSomewhere(string provider)
    {
        var usages = ParseByteAtATime(provider).Select(UsageIn).Where(u => u is not null).ToList();

        Assert.NotEmpty(usages);
        Assert.True(usages[^1]!.Value.GetProperty("prompt_tokens").GetInt32() > 0);
    }

    /// <summary>
    /// Moonshot reports usage TWICE — nested on the finish chunk and again at the
    /// top level on a trailing empty-choices chunk. Accumulating the two would
    /// double every Moonshot bill, so the reader must take last-writer-wins.
    /// </summary>
    [Fact]
    public void Moonshot_ReportsUsageTwice()
    {
        var usages = ParseByteAtATime("moonshot").Select(UsageIn).Where(u => u is not null).ToList();

        Assert.Equal(2, usages.Count);
        Assert.Equal(
            usages[0]!.Value.GetProperty("total_tokens").GetInt32(),
            usages[1]!.Value.GetProperty("total_tokens").GetInt32());
    }

    /// <summary>
    /// Reasoning tokens are counted inside <c>completion_tokens</c>, not beside
    /// them. On this capture every completion token was a reasoning token, which
    /// is why an output estimate derived from answer length under-counts badly.
    /// </summary>
    [Fact]
    public void Zai_CountsReasoningTokensInsideCompletionTokens()
    {
        var usage = ParseByteAtATime("zai").Select(UsageIn).Last(u => u is not null)!.Value;

        var completion = usage.GetProperty("completion_tokens").GetInt32();
        var reasoning = usage.GetProperty("completion_tokens_details")
            .GetProperty("reasoning_tokens").GetInt32();

        Assert.Equal(completion, reasoning);
        Assert.True(reasoning > 0);
    }

    /// <summary>
    /// Hugging Face echoes the requested model id lower-cased, so feeding a
    /// response id back into a retry would ask for a model that does not exist.
    /// </summary>
    [Fact]
    public void HuggingFace_LowercasesTheModelIdInItsResponse()
    {
        var first = ParseByteAtATime("hf").First(f => f.Kind == SseFrameKind.Event);

        using var doc = JsonDocument.Parse(first.Data);
        var model = doc.RootElement.GetProperty("model").GetString();

        Assert.NotNull(model);
        Assert.Equal(model!.ToLowerInvariant(), model);
        Assert.Contains("qwen3-coder", model, StringComparison.Ordinal);
    }
}
