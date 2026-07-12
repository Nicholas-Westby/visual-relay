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
    // DeepSeek time-of-day peak pricing (provisional, 2026-07-07):
    // 2× during 09:00–12:00 and 14:00–18:00 Asia/Shanghai (UTC+8, no DST).
    // Announced 2026-06-30 (TechNode), effective mid-July 2026 with V4 release.
    // Official docs (api-docs.deepseek.com/quick_start/pricing) had not published
    // the schedule as of 2026-07-07 — update when published.
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
            // deepseek-v4-flash (api-docs.deepseek.com, 2026-07-07); off-peak base rates
            ["deepseek-v4-flash"] = new(0.14, 0.28, 0.0028, 0.14) { Windows = DeepseekPeakWindows },
            // deepseek-v4-pro (api-docs.deepseek.com, 2026-07-07); off-peak base rates
            ["deepseek-v4-pro"] = new(0.435, 0.87, 0.003625, 0.435) { Windows = DeepseekPeakWindows },
            // GLM 5.2 via HF (zai-org), 2026-07-07; CacheWrite falls back to Input (1.40)
            ["glm-5.2"] = new(1.40, 4.40, 0.26),
            // Qwen3-VL-235B-A22B-Instruct (openrouter.ai, 2026-07-07)
            ["hf-qwen3-vl-235b"] = new(0.20, 0.88),
            // Opus (docs.anthropic.com, 2026-07-07); cache hit 0.1×, write 1.25× (5-min TTL)
            ["claude-opus-1m"] = new(5.0, 25.0, 0.50, 6.25),
            // Sonnet (docs.anthropic.com, 2026-07-07); sticker $3/$15; Sonnet 5 intro $2/$10 through 2026-08-31
            ["claude-sonnet"] = new(3.0, 15.0, 0.30, 3.75),
            // GPT-5 (openrouter.ai, 2026-07-07); cached input 0.1×
            ["gpt-5"] = new(1.25, 10.0, 0.125),
            // Qwen3-Coder-480B-A35B-Instruct via Novita; verify rate on HF model page (not verified 2026-07-07)
            ["hf-qwen3-coder-next"] = new(0.30, 1.30),
            // kimi-k2.7-code (platform.kimi.ai/docs/pricing, 2026-07-07)
            ["kimi-k2"] = new(0.95, 4.0, 0.19, 0.95),
        };
}
