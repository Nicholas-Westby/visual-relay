using System.Text.Json;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Covers <see cref="ProviderUsageReader"/> against the three places the four
/// providers put <c>usage</c>, and against the double report that would double a
/// Moonshot bill if the reader accumulated instead of replacing.
/// </summary>
public sealed class ProviderUsageReaderTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>DeepSeek and Z.AI put usage at the top level of the finish chunk.</summary>
    [Fact]
    public void TopLevelUsage_IsRead()
    {
        var usage = ProviderUsageReader.TryRead(Parse(
            """{"choices":[{"finish_reason":"stop"}],"usage":{"prompt_tokens":6,"completion_tokens":9}}"""));

        Assert.NotNull(usage);
        Assert.Equal(6, usage!.PromptTokens);
        Assert.Equal(9, usage.CompletionTokens);
    }

    /// <summary>Moonshot nests it inside the first choice on the finish chunk.</summary>
    [Fact]
    public void NestedChoiceUsage_IsRead()
    {
        var usage = ProviderUsageReader.TryRead(Parse(
            """{"choices":[{"finish_reason":"stop","usage":{"prompt_tokens":11,"completion_tokens":4}}]}"""));

        Assert.NotNull(usage);
        Assert.Equal(11, usage!.PromptTokens);
    }

    /// <summary>Hugging Face sends it on a trailing chunk with no choices at all.</summary>
    [Fact]
    public void TrailingEmptyChoicesUsage_IsRead()
    {
        var usage = ProviderUsageReader.TryRead(Parse(
            """{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":40}}"""));

        Assert.NotNull(usage);
        Assert.Equal(40, usage!.CompletionTokens);
    }

    /// <summary>A content delta carries no usage and must not invent one.</summary>
    [Fact]
    public void ContentDelta_HasNoUsage()
    {
        Assert.Null(ProviderUsageReader.TryRead(Parse(
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":null}],"usage":null}""")));
    }

    /// <summary>
    /// The load-bearing rule: a second report of the same call replaces the
    /// first. Summing them would bill Moonshot twice for every request.
    /// </summary>
    [Fact]
    public void RepeatedUsage_ReplacesRatherThanAccumulates()
    {
        var first = ProviderUsageReader.TryRead(Parse(
            """{"choices":[{"usage":{"prompt_tokens":11,"completion_tokens":4}}]}"""));
        var second = ProviderUsageReader.TryRead(Parse(
            """{"choices":[],"usage":{"prompt_tokens":11,"completion_tokens":4}}"""));

        var merged = ProviderUsageReader.Merge(first, second);

        Assert.Equal(11, merged!.PromptTokens);
        Assert.Equal(4, merged.CompletionTokens);
    }

    /// <summary>A chunk with no usage leaves the running value untouched.</summary>
    [Fact]
    public void MergeWithNull_KeepsCurrent()
    {
        var current = new ProviderUsage(7, 3);

        Assert.Same(current, ProviderUsageReader.Merge(current, null));
    }

    /// <summary>
    /// Cached tokens are a subset of the prompt total, so uncached is the
    /// difference. Treating the two as disjoint over-bills the input.
    /// </summary>
    [Fact]
    public void CachedTokens_AreSubtractedFromPromptTokens()
    {
        var usage = ProviderUsageReader.TryRead(Parse(
            """{"usage":{"prompt_tokens":1000,"completion_tokens":10,"prompt_tokens_details":{"cached_tokens":300}}}"""));

        Assert.Equal(300, usage!.CachedTokens);
        Assert.Equal(700, usage.UncachedPromptTokens);
    }

    /// <summary>DeepSeek's explicit hit count is used when the details object omits it.</summary>
    [Fact]
    public void DeepSeekCacheHitTokens_AreUsedAsAFallback()
    {
        var usage = ProviderUsageReader.TryRead(Parse(
            """{"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_cache_hit_tokens":40}}"""));

        Assert.Equal(40, usage!.CachedTokens);
        Assert.Equal(60, usage.UncachedPromptTokens);
    }

    /// <summary>
    /// Reasoning tokens are already inside the completion count, so they are
    /// surfaced for visibility but never added to it.
    /// </summary>
    [Fact]
    public void ReasoningTokens_AreReportedInsideCompletionTokens()
    {
        var usage = ProviderUsageReader.TryRead(Parse(
            """{"usage":{"prompt_tokens":7,"completion_tokens":40,"completion_tokens_details":{"reasoning_tokens":40}}}"""));

        Assert.Equal(40, usage!.CompletionTokens);
        Assert.Equal(40, usage.ReasoningTokens);
        Assert.Equal(47, usage.TotalTokens);
    }

    /// <summary>
    /// Hugging Face sends both details objects as explicit JSON nulls rather than
    /// omitting them. Reading through a null must not throw: this shape came off
    /// the real wire and crashed the first version of this reader.
    /// </summary>
    [Fact]
    public void ExplicitNullDetailsObjects_ReadAsZeroNotAThrow()
    {
        var usage = ProviderUsageReader.TryRead(Parse(
            """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12,"prompt_tokens_details":null,"completion_tokens_details":null}}"""));

        Assert.NotNull(usage);
        Assert.Equal(10, usage!.PromptTokens);
        Assert.Equal(10, usage.UncachedPromptTokens);
        Assert.Equal(0, usage.CachedTokens);
        Assert.Equal(0, usage.ReasoningTokens);
    }

    /// <summary>An explicitly reported cache-write count is carried through.</summary>
    [Fact]
    public void CacheWriteTokens_AreCarried()
    {
        var usage = ProviderUsageReader.TryRead(Parse(
            """{"usage":{"prompt_tokens":100,"completion_tokens":5,"cache_creation_input_tokens":40}}"""));

        Assert.Equal(40, usage!.CacheWriteTokens);
    }

    /// <summary>A usage object missing every field reads as zeros, not a throw.</summary>
    [Fact]
    public void EmptyUsageObject_ReadsAsZero()
    {
        var usage = ProviderUsageReader.TryRead(Parse("""{"usage":{}}"""));

        Assert.NotNull(usage);
        Assert.Equal(0, usage!.PromptTokens);
        Assert.Equal(0, usage.UncachedPromptTokens);
    }
}
