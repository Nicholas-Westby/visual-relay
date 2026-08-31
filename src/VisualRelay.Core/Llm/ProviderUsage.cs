namespace VisualRelay.Core.Llm;

/// <summary>
/// Measured token usage for one model call, read from the provider rather than
/// estimated. Replaces the old <c>answer.Length / 4</c> heuristic, which
/// under-counted output by close to an order of magnitude on a reasoning model.
/// </summary>
/// <param name="PromptTokens">
/// Total input tokens. <paramref name="CachedTokens"/> is a SUBSET of this, not
/// a sibling: uncached input is the difference, never the raw total.
/// </param>
/// <param name="CompletionTokens">
/// Total output tokens. On every reasoning provider measured this INCLUDES
/// <paramref name="ReasoningTokens"/>; none of them document that, and on one
/// GLM call reasoning was 40 of 40 completion tokens.
/// </param>
/// <param name="CachedTokens">Input tokens served from the provider's cache.</param>
/// <param name="ReasoningTokens">
/// The reasoning share of <paramref name="CompletionTokens"/>. Reported for
/// visibility; it must not be added to the completion count when costing.
/// </param>
/// <param name="CacheWriteTokens">Input tokens written to the cache, when reported.</param>
public sealed record ProviderUsage(
    int PromptTokens,
    int CompletionTokens,
    int CachedTokens = 0,
    int ReasoningTokens = 0,
    int CacheWriteTokens = 0)
{
    /// <summary>
    /// Input tokens actually billed at the uncached rate: the difference, since
    /// cached tokens are a subset of the prompt total.
    /// </summary>
    public int UncachedPromptTokens => Math.Max(0, PromptTokens - CachedTokens);

    /// <summary>Total tokens across input and output.</summary>
    public int TotalTokens => PromptTokens + CompletionTokens;
}
