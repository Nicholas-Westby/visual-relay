using System.Globalization;
using System.Text.Json;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Core.Costs;

public sealed record RelayCostEstimate(
    string Model,
    double CostUsd,
    bool Priced,
    int PromptTokens,
    int CachedTokens,
    int OutputTokens,
    double DurationSeconds,
    int CacheWriteTokens = 0,
    int Turns = 0);

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
    /// When the recorded model name is a tier alias (e.g. "cheap", "balanced"),
    /// it is resolved to a concrete model via
    /// <see cref="BackendConfigGenerator.DefaultTierResolution"/> before pricing.
    /// Per-run tier overrides are not recorded in reports, so the *default*
    /// resolution is used — this is an accepted approximation that is strictly
    /// better than the previous hand-copied snapshot, which had the same
    /// staleness plus drift.
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
    /// Worked example (stage 4, balanced, 18 turns):
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
    ///
    /// Schedule evaluation uses the report's stage-end <c>timestamp</c>. Individual
    /// <c>llm_call</c> entries carry no timestamps, and stages are bounded by the
    /// stage ceiling, so at most one rate-window boundary crossing can occur per
    /// stage — this approximation is acceptable.
    /// </summary>
    /// <param name="report">The stage report JSON to price (model, timeline, timestamp).</param>
    /// <param name="evaluationInstant">
    /// Optional UTC instant for rate-schedule evaluation (e.g. time-of-day windows).
    /// When <c>null</c>, the report's top-level <c>timestamp</c> field is used.
    /// When absent or unparseable, no windows match (multiplier 1×).
    /// </param>
    public static RelayCostEstimate EstimateReport(JsonElement report, DateTime? evaluationInstant = null)
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

        if (!RelayPricing.Default.TryGetValue(model, out var pricing) &&
            !(BackendConfigGenerator.DefaultTierResolution.TryGetValue(model, out var concrete) &&
              RelayPricing.Default.TryGetValue(concrete, out pricing)))
        {
            return new RelayCostEstimate(model, 0, false, uncachedTokens, cachedTokens, outputTokens, duration, cacheWriteTokens, llmCalls.Length);
        }

        var instant = evaluationInstant ?? ReadTimestamp(report);
        var multiplier = GetScheduleMultiplier(pricing, instant);

        var usd = (
            uncachedTokens * pricing.Input +
            cachedTokens * pricing.EffectiveCachedInput +
            cacheWriteTokens * pricing.EffectiveCacheWrite +
            outputTokens * pricing.Output
        ) * multiplier / 1_000_000d;
        return new RelayCostEstimate(model, usd, true, uncachedTokens, cachedTokens, outputTokens, duration, cacheWriteTokens, llmCalls.Length);
    }

    /// <summary>
    /// Compute the rate multiplier from time-of-day windows attached to a pricing entry.
    /// Returns 1.0 when no windows match (or no windows are configured, or the instant
    /// is <see cref="DateTime.MinValue"/> indicating no timestamp was available).
    /// </summary>
    private static double GetScheduleMultiplier(ModelPricing pricing, DateTime utcInstant)
    {
        if (pricing.Windows is null or { Count: 0 })
        {
            return 1.0;
        }

        if (utcInstant == DateTime.MinValue)
        {
            return 1.0; // no timestamp available — default to base rates
        }

        foreach (var window in pricing.Windows)
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId);
                var local = TimeZoneInfo.ConvertTimeFromUtc(utcInstant, tz);
                var localTime = TimeOnly.FromDateTime(local);
                if (localTime >= window.StartLocal && localTime < window.EndLocal)
                {
                    return window.Multiplier;
                }
            }
            catch (TimeZoneNotFoundException)
            {
                // Unknown timezone — skip this window (should not happen in production).
            }
        }

        return 1.0;
    }

    /// <summary>
    /// Read the report's top-level <c>timestamp</c> as a UTC <see cref="DateTime"/>.
    /// Returns <see cref="DateTime.MinValue"/> when the field is absent or unparseable,
    /// which causes all rate windows to miss (multiplier 1×).
    /// </summary>
    private static DateTime ReadTimestamp(JsonElement report)
    {
        if (report.TryGetProperty("timestamp", out var ts) &&
            ts.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(ts.GetString(), null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return DateTime.MinValue;
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
