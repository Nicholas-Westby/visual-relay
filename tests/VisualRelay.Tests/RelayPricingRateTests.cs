using System.Text.Json;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

public sealed class RelayPricingRateTests
{
    [Fact]
    public void Glm53FlashOverHf_UsesTheSameZaiPublishedRates()
    {
        // hf-glm-5.3-flash reaches the same upstream model as the first-party
        // route, so it carries Z.AI's published rates: input 0.15, cached 0.03,
        // output 0.50.
        // uncached=2000, cached=500, output=ceil(40/4)+50=60.
        // cost = (2000*0.15 + 500*0.03 + 60*0.50) / 1_000_000
        //      = (300 + 15 + 30) / 1_000_000 = 345 / 1_000_000 = 0.000345.
        using var document = JsonDocument.Parse(
            $$"""
            {
              "model": "hf-glm-5.3-flash",
              "result": { "answer": "{{new string('y', 40)}}" },
              "stats": { "prompt_cache": { "cached_tokens": 500 } },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 2000 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("hf-glm-5.3-flash", cost.Model);
        Assert.Equal(0.000345, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void KimiK2_IncludesCacheWriteRate()
    {
        // kimi-k2: input 0.95, cached 0.19, cache-write 0.95, output 4.0.
        // uncached=1000, cached=200, cache-write=150, output=ceil(64/4)+3*50=166.
        // cost = (1000*0.95 + 200*0.19 + 150*0.95 + 166*4.0) / 1_000_000
        //      = (950 + 38 + 142.5 + 664) / 1_000_000 = 1794.5 / 1_000_000 = 0.0017945.
        using var document = JsonDocument.Parse(
            """
            {
              "model": "kimi-k2",
              "result": { "answer": "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!?" },
              "stats": {
                "prompt_cache": { "cached_tokens": 200, "cache_write_tokens": 150 }
              },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 500 },
                { "type": "llm_call", "prompt_tokens_est": 800 },
                { "type": "llm_call", "prompt_tokens_est": 1000 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("kimi-k2", cost.Model);
        Assert.Equal(0.0017945, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void KimiK2_UpdatedCachedInputRate()
    {
        // kimi-k2: input 0.95, cached 0.19, cache-write 0.95, output 4.0.
        // uncached=1000, cached=300, output=ceil(40/4)+50=60.
        // cost = (1000*0.95 + 300*0.19 + 60*4.0) / 1_000_000
        //      = (950 + 57 + 240) / 1_000_000 = 1247 / 1_000_000 = 0.001247.
        using var document = JsonDocument.Parse(
            $$"""
            {
              "model": "kimi-k2",
              "result": { "answer": "{{new string('y', 40)}}" },
              "stats": { "prompt_cache": { "cached_tokens": 300 } },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 1000 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("kimi-k2", cost.Model);
        Assert.Equal(0.001247, cost.CostUsd, precision: 10);
    }

    /// <summary>
    /// A model with an explicit cached-input rate but no cache-write rate: the
    /// cache-write rate must fall back to input, and the cached rate must not.
    /// </summary>
    [Fact]
    public void GlmFlash_IncludesCachedInputRate()
    {
        // glm-5.3-flash: input 0.15, cached 0.03, output 0.50, cache-write unset.
        // uncached=1000, cached=500, output=ceil(64/4)+2*50=116.
        // cost = (1000*0.15 + 500*0.03 + 116*0.50) / 1_000_000
        //      = (150 + 15 + 58) / 1_000_000 = 223 / 1_000_000 = 0.000223.
        using var document = JsonDocument.Parse(
            $$"""
            {
              "model": "glm-5.3-flash",
              "result": { "answer": "{{new string('x', 64)}}" },
              "stats": { "prompt_cache": { "cached_tokens": 500 } },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 500 },
                { "type": "llm_call", "prompt_tokens_est": 1000 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("glm-5.3-flash", cost.Model);
        Assert.Equal(0.000223, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void HfQwen3CoderNext_VerifiedNovitaRates()
    {
        // Qwen3-Coder-480B-A35B-Instruct via Novita serverless — VERIFIED 2026-08-17
        // (0.38/1.55), superseding the earlier unverified 0.30/1.30 placeholder.
        // uncached=1000, output=ceil(4/4)+50=51.
        // cost = (1000*0.38 + 51*1.55) / 1_000_000 = (380 + 79.05) / 1_000_000 = 0.00045905.
        using var document = JsonDocument.Parse(
            """
            {
              "model": "hf-qwen3-coder-next",
              "result": { "answer": "test" },
              "stats": {},
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 1000 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("hf-qwen3-coder-next", cost.Model);
        Assert.Equal(0.00045905, cost.CostUsd, precision: 10);
    }

    // ── Effective-rate helpers ─────────────────────────────────────

    [Fact]
    public void EffectiveCachedInput_ReturnsExplicitValueWhenSet()
    {
        var pricing = new ModelPricing(0.14, 0.28, CachedInput: 0.0028, CacheWrite: 0.14);
        Assert.Equal(0.0028, pricing.EffectiveCachedInput);
    }

    [Fact]
    public void EffectiveCachedInput_ReturnsInputWhenNull()
    {
        var pricing = new ModelPricing(0.20, 0.88);
        Assert.Equal(0.20, pricing.EffectiveCachedInput);
    }

    [Fact]
    public void EffectiveCacheWrite_ReturnsExplicitValueWhenSet()
    {
        var pricing = new ModelPricing(5.0, 25.0, CachedInput: 0.50, CacheWrite: 6.25);
        Assert.Equal(6.25, pricing.EffectiveCacheWrite);
    }

    [Fact]
    public void EffectiveCacheWrite_ReturnsInputWhenNull()
    {
        var pricing = new ModelPricing(1.40, 4.40, CachedInput: 0.26);
        Assert.Equal(1.40, pricing.EffectiveCacheWrite);
    }
}
