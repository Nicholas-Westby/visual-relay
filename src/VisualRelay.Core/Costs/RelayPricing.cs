namespace VisualRelay.Core.Costs;

// Rates are USD per 1,000,000 tokens, matching Relay's pricing.json unit.
// CacheWrite: when null, cache-write tokens are billed at the Input rate (the
// industry-standard fallback — the provider charges for writing to the cache).
internal sealed record ModelPricing(
    double Input,
    double Output,
    double? CachedInput = null,
    double? CacheWrite = null,
    IReadOnlyList<RateWindow>? Windows = null)
{
    /// <summary>Effective cached-input rate: the explicit value when set,
    /// otherwise falls back to <see cref="Input"/> (the estimator rule).</summary>
    public double EffectiveCachedInput => CachedInput ?? Input;

    /// <summary>Effective cache-write rate: the explicit value when set,
    /// otherwise falls back to <see cref="Input"/> (the estimator rule).</summary>
    public double EffectiveCacheWrite => CacheWrite ?? Input;
}

internal static class RelayPricing
{
    // DeepSeek time-of-day peak pricing (CONFIRMED 2026-08-17 against
    // api-docs.deepseek.com/quick_start/pricing, which now publishes the schedule):
    // 2× during 09:00–12:00 and 14:00–18:00 Asia/Shanghai (UTC+8, no DST).
    // The docs state the windows as 01:00–04:00 and 06:00–10:00 UTC — identical
    // instants, since Asia/Shanghai is a fixed UTC+8 with no daylight saving.
    // Kept in Asia/Shanghai because that is the zone DeepSeek prices against, so a
    // future UTC-offset change would be a data edit here rather than silent drift.
    private static readonly RateWindow[] DeepseekPeakWindows =
    [
        new(new(9, 0), new(12, 0), "Asia/Shanghai", 2.0),
        new(new(14, 0), new(18, 0), "Asia/Shanghai", 2.0),
    ];

    /// <summary>Concrete-model-only pricing. Tier-alias lookups resolve via
    /// <see cref="Configuration.BackendConfigGenerator.DefaultTierResolution"/>.</summary>
    public static IReadOnlyDictionary<string, ModelPricing> Default { get; } =
        new Dictionary<string, ModelPricing>(StringComparer.Ordinal)
        {
            // deepseek-v4-flash → DeepSeek-V4-Flash-0731 (api-docs.deepseek.com, 2026-08-17);
            // off-peak base rates. CacheWrite == Input because DeepSeek bills a cache miss
            // at the standard input rate and charges nothing extra to populate the cache.
            ["deepseek-v4-flash"] = new(0.22, 0.66, 0.007, 0.22) { Windows = DeepseekPeakWindows },
            // deepseek-v4-pro → DeepSeek-V4-Pro-0813 (api-docs.deepseek.com, 2026-08-17);
            // off-peak base rates; same cache-write rationale as -flash above.
            ["deepseek-v4-pro"] = new(0.66, 1.98, 0.022, 0.66) { Windows = DeepseekPeakWindows },
            // GLM 5.2 via HF (zai-org), 2026-08-17; CacheWrite falls back to Input (1.40)
            ["glm-5.2"] = new(1.40, 4.40, 0.26),
            // Qwen3-VL-235B-A22B-Instruct (openrouter.ai, 2026-08-17)
            ["hf-qwen3-vl-235b"] = new(0.20, 0.88),
            // Qwen3-VL-30B-A3B-Instruct (openrouter.ai, 2026-08-17); the vision-tier
            // fallback — priced so a describe pre-step that fails over is still costed.
            ["hf-qwen3-vl-30b"] = new(0.13, 0.52),
            // Opus (platform.claude.com, 2026-08-17); cache hit 0.1×, write 1.25× (5-min TTL)
            ["claude-opus-1m"] = new(5.0, 25.0, 0.50, 6.25),
            // Sonnet (platform.claude.com, 2026-08-17); sticker $3/$15 — Sonnet 5 is on an
            // intro $2/$10 until 2026-08-31, so sticker avoids under-costing after that.
            ["claude-sonnet"] = new(3.0, 15.0, 0.30, 3.75),
            // GPT-5 (developers.openai.com/api/docs/pricing, 2026-08-17); cached input 0.1×
            ["gpt-5"] = new(1.25, 10.0, 0.125),
            // Qwen3-Coder-480B-A35B-Instruct via Novita (novita.ai serverless, 2026-08-17);
            // now verified — supersedes the earlier unverified 0.30/1.30 placeholder.
            ["hf-qwen3-coder-next"] = new(0.38, 1.55),
            // kimi-k2.7-code (platform.kimi.ai/docs/pricing, 2026-08-17)
            ["kimi-k2"] = new(0.95, 4.0, 0.19, 0.95),
        };
}
