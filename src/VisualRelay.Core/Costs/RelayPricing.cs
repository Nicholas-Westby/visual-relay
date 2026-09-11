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
    // DeepSeek time-of-day peak pricing (RE-CONFIRMED 2026-08-31 against
    // api-docs.deepseek.com/quick_start/pricing): 2× during 09:00–12:00 and
    // 14:00–18:00 Asia/Shanghai (UTC+8, no DST), MONDAY THROUGH FRIDAY.
    // The docs state the windows as 01:00–04:00 and 06:00–10:00 UTC — identical
    // instants, since Asia/Shanghai is a fixed UTC+8 with no daylight saving.
    // Kept in Asia/Shanghai because that is the zone DeepSeek prices against, so a
    // future UTC-offset change would be a data edit here rather than silent drift.
    // The weekday restriction landed 2026-08-23, six days after the windows were
    // first recorded here: DeepSeek moved Saturday and Sunday to off-peak rates
    // outright, so an all-day window double-charged seven hours of every weekend.
    private static readonly RateWindow[] DeepseekPeakWindows =
    [
        new(new(9, 0), new(12, 0), "Asia/Shanghai", 2.0, RateWindow.Weekdays),
        new(new(14, 0), new(18, 0), "Asia/Shanghai", 2.0, RateWindow.Weekdays),
    ];

    /// <summary>Concrete-model-only pricing. Tier-alias lookups resolve via
    /// <see cref="Configuration.ModelCatalog.DefaultTierResolution"/>.</summary>
    public static IReadOnlyDictionary<string, ModelPricing> Default { get; } =
        new Dictionary<string, ModelPricing>(StringComparer.Ordinal)
        {
            // deepseek-flash → DeepSeek-V4.1-Flash (api-docs.deepseek.com/quick_start/pricing,
            // 2026-09-10); off-peak base rates. CacheWrite == Input because DeepSeek bills a
            // cache miss at the standard input rate and charges nothing extra to populate
            // the cache. The three V4 rows below are the same model upstream and so carry
            // the same figures.
            ["deepseek-flash"] = new(0.15, 0.60, 0.003, 0.15) { Windows = DeepseekPeakWindows },
            // deepseek-v4-flash → DeepSeek-V4-Flash-0731, RETIRED 2026-09-10 and routed to
            // V4.1 Flash, so it is billed at the V4.1 rates above rather than the 0.22 /
            // 0.66 / 0.007 it used to publish. Those rates are no longer offered anywhere.
            ["deepseek-v4-flash"] = new(0.15, 0.60, 0.003, 0.15) { Windows = DeepseekPeakWindows },
            // deepseek-v4-pro → DeepSeek-V4-Pro-0813. DeepSeek serves it until
            // 2026-09-14 04:00 UTC and routes it to V4.1 Flash at the V4.1 price from
            // then, which is what this row records. Until that date a hop that really
            // lands on V4-Pro is billed here at roughly a QUARTER of its true rate, and
            // that hop is not remote: V4-Pro is the FIRST fallback on balanced, the tier
            // six of the twelve stages run on, reached as soon as the head's two
            // attempts fail. It is also the fourth frontier hop and the third cheap
            // fallback. Under-counting for these few days is the deliberate lesser
            // evil: recording rates that expire on 2026-09-14 would over-count every
            // V4-Pro hop from then on, and those hops keep arriving.
            ["deepseek-v4-pro"] = new(0.15, 0.60, 0.003, 0.15) { Windows = DeepseekPeakWindows },
            // deepseek-v4-flash-vision-exp → DeepSeek-V4-Flash-Vision-Exp, likewise
            // retired 2026-09-10 and routed to V4.1 Flash, which is natively multimodal.
            // No separate image rate is recorded because DeepSeek bills images AS input
            // tokens, at the Input rate above. This used to rest on a second, now-false
            // claim — that images never reached DeepSeek at all. They do: image parts are
            // sent verbatim on the direct path. The rate still holds, because the
            // estimator prices the provider's own prompt_tokens, which already include
            // the image tokens.
            ["deepseek-v4-flash-vision-exp"] = new(0.15, 0.60, 0.003, 0.15) { Windows = DeepseekPeakWindows },
            // GLM 5.3 Flash first-party on Z.AI (docs.z.ai/guides/overview/pricing,
            // 2026-08-26). These are the sticker rates: Z.AI is running a 50%-off
            // promotion on this model until 2026-09-09, and pricing the promo
            // would under-count every run made after it lapses, the same call the
            // vision entries below make. CacheWrite falls back to Input (0.15) —
            // Z.AI publishes no separate cache-write rate.
            ["glm-5.3-flash"] = new(0.15, 0.50, 0.03),
            // The same model over HF Inference Providers, provider-pinned to
            // zai-org, so Z.AI's rates apply unchanged; CacheWrite falls back to
            // Input (0.15).
            ["hf-glm-5.3-flash"] = new(0.15, 0.50, 0.03),
            // Qwen3-VL-235B-A22B-Instruct, and the -30B vision-tier fallback below,
            // priced from HF's live catalog (router.huggingface.co/v1/models,
            // 2026-08-31) rather than an aggregator. Both are routed UNPINNED, so HF
            // picks the serving provider per request and each has two live rates:
            // 235B is 0.30/1.50 on novita and 0.20/0.88 on deepinfra; 30B is
            // 0.20/0.70 and 0.15/0.60. Recording the dearest of each is the same call
            // the entries above make for sticker over promotional rates — pricing the
            // cheaper host under-bills every request HF sends to the other one. Pin a
            // ":provider" suffix on the route if the envelope ever needs to be fixed;
            // note the pinned form also fixes the context window, which likewise
            // differs between the two hosts.
            ["hf-qwen3-vl-235b"] = new(0.30, 1.50),
            ["hf-qwen3-vl-30b"] = new(0.20, 0.70),
            // Qwen3-Coder-480B-A35B-Instruct via Novita (novita.ai serverless, 2026-08-17);
            // now verified — supersedes the earlier unverified 0.30/1.30 placeholder.
            ["hf-qwen3-coder-next"] = new(0.38, 1.55),
            // kimi-k2.7-code (platform.kimi.ai/docs/pricing, 2026-08-17)
            ["kimi-k2"] = new(0.95, 4.0, 0.19, 0.95),
        };
}
