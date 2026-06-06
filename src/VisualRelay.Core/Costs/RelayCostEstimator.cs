using System.Text.Json;

namespace VisualRelay.Core.Costs;

public sealed record RelayCostEstimate(
    string Model,
    double CostUsd,
    bool Priced,
    int PromptTokens,
    int CachedTokens,
    int OutputTokens,
    double DurationSeconds,
    int CacheWriteTokens = 0);

public static class RelayCostEstimator
{
    private const int OutputTokensPerTurn = 50;

    public static RelayCostEstimate EstimateReport(string reportPath)
    {
        using var stream = File.OpenRead(reportPath);
        using var document = JsonDocument.Parse(stream);
        return EstimateReport(document.RootElement);
    }

    /// <summary>
    /// Estimate the USD cost for a single-stage report file.
    ///
    /// Token accounting model (per-turn incremental, NOT cumulative-sum-minus-cached):
    /// Each turn's <c>prompt_tokens_est</c> in the timeline is the CUMULATIVE context
    /// size for that turn — it grows monotonically as the conversation adds turns.
    /// Summing them over-counts (e.g. 585,901 for stage 4 vs the true 44,038 tokens).
    /// The buggy formula <c>sum − cached_tokens</c> collapsed uncached input to near
    /// $0 because <c>cached_tokens</c> is itself cumulative and often larger than
    /// the cumulative sum of per-turn contexts.
    ///
    /// The correct uncached input is the INCREMENTAL new context per turn:
    ///   uncached = context[0] + Σ max(0, context[i] − context[i−1])
    /// which telescopes to context[last] (the final cumulative context) because
    /// context is monotonically non-decreasing within a single stage conversation.
    ///
    /// Worked example (stage 4, balanced-kimi, 18 turns):
    ///   Turn  1:  8619 → delta =  8619
    ///   Turn  2: 14630 → delta =  6011
    ///   ...
    ///   Turn 18: 44038 → delta =    96
    ///   Total deltas = 44,038 (= final context, as it telescopes)
    ///   cached_tokens   = 650,240 (cumulative cache reads across all turns)
    ///   cache_write_tokens = 0
    ///
    ///   Cost = 44,038 × $0.435/M + 650,240 × $0.003625/M + output × $0.87/M
    ///        = $0.01916 + $0.00236 + output component
    ///        = $0.02328 (vs ~$0.004 from the old buggy formula)
    ///
    /// Output tokens are estimated (not measured) because the reports contain no
    /// real output-token field. The approximation is:
    ///   ceil(answer.Length / 4) + turns × <see cref="OutputTokensPerTurn"/>
    /// where the constant 50 tokens/turn accounts for reasoning overhead in tool-use
    /// responses that precede the final answer.
    /// </summary>
    public static RelayCostEstimate EstimateReport(JsonElement report)
    {
        var model = ReadString(report, "model");
        var llmCalls = report.TryGetProperty("timeline", out var timeline) && timeline.ValueKind == JsonValueKind.Array
            ? timeline.EnumerateArray().Where(IsLlmCall).ToArray()
            : [];

        // Per-turn incremental context: uncached input telescopes to the final
        // cumulative context (context is monotonic within a single stage).
        var contexts = llmCalls
            .Select(call => ReadInt(call, "prompt_tokens_est"))
            .ToArray();
        var uncachedTokens = contexts.Length > 0 ? contexts[^1] : 0;

        var answer = report.TryGetProperty("result", out var result) ? ReadString(result, "answer") : string.Empty;
        // Output tokens are estimated — the reports lack a measured output-token field.
        var outputTokens = (int)Math.Ceiling(answer.Length / 4.0) + llmCalls.Length * OutputTokensPerTurn;
        var stats = report.TryGetProperty("stats", out var statsValue) ? statsValue : default;
        var (cachedTokens, cacheWriteTokens) = ReadPromptCache(stats);
        var duration = ReadDouble(stats, "total_llm_time_s") + ReadDouble(stats, "total_tool_time_s");

        if (!RelayPricing.Default.TryGetValue(model, out var pricing))
        {
            return new RelayCostEstimate(model, 0, false, uncachedTokens, cachedTokens, outputTokens, duration, cacheWriteTokens);
        }

        var cachedRate = pricing.CachedInput ?? pricing.Input;
        var cacheWriteRate = pricing.CacheWrite ?? pricing.Input;
        var usd = (
            uncachedTokens * pricing.Input +
            cachedTokens * cachedRate +
            cacheWriteTokens * cacheWriteRate +
            outputTokens * pricing.Output
        ) / 1_000_000d;
        return new RelayCostEstimate(model, usd, true, uncachedTokens, cachedTokens, outputTokens, duration, cacheWriteTokens);
    }

    private static bool IsLlmCall(JsonElement item) =>
        item.TryGetProperty("type", out var type) && type.GetString() == "llm_call";

    private static (int cachedTokens, int cacheWriteTokens) ReadPromptCache(JsonElement stats)
    {
        if (stats.ValueKind != JsonValueKind.Object ||
            !stats.TryGetProperty("prompt_cache", out var cache) ||
            cache.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }

        return (ReadInt(cache, "cached_tokens"), ReadInt(cache, "cache_write_tokens"));
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static double ReadDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetDouble(out var parsed)
            ? parsed
            : 0;
}
