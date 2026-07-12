using System.Text.Json;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

public sealed class RelayPricingRateTests
{
    [Fact]
    public void Glm52_UpdatedInputAndCachedRates()
    {
        // GLM 5.2: input 1.40, cached 0.26, output 4.40.
        // uncached=2000, cached=500, output=ceil(40/4)+50=60.
        // cost = (2000*1.40 + 500*0.26 + 60*4.40) / 1_000_000
        //      = (2800 + 130 + 264) / 1_000_000 = 3194 / 1_000_000 = 0.003194.
        using var document = JsonDocument.Parse(
            $$"""
            {
              "model": "glm-5.2",
              "result": { "answer": "{{new string('y', 40)}}" },
              "stats": { "prompt_cache": { "cached_tokens": 500 } },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 2000 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("glm-5.2", cost.Model);
        Assert.Equal(0.003194, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void ClaudeOpus_IncludesCacheWriteRate()
    {
        // Claude Opus: input 5.0, cached 0.50, cache-write 6.25, output 25.0.
        // uncached=1000, cached=200, cache-write=150, output=ceil(64/4)+3*50=166.
        // cost = (1000*5.0 + 200*0.50 + 150*6.25 + 166*25.0) / 1_000_000
        //      = (5000 + 100 + 937.5 + 4150) / 1_000_000 = 10187.5 / 1_000_000 = 0.0101875.
        using var document = JsonDocument.Parse(
            """
            {
              "model": "claude-opus-1m",
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
        Assert.Equal("claude-opus-1m", cost.Model);
        Assert.Equal(0.0101875, cost.CostUsd, precision: 10);
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

    [Fact]
    public void Gpt5_IncludesCachedInputRate()
    {
        // GPT-5: input 1.25, cached 0.125, output 10.0.
        // uncached=1000, cached=500, output=ceil(64/4)+2*50=116.
        // cost = (1000*1.25 + 500*0.125 + 116*10.0) / 1_000_000
        //      = (1250 + 62.5 + 1160) / 1_000_000 = 2472.5 / 1_000_000 = 0.0024725.
        using var document = JsonDocument.Parse(
            $$"""
            {
              "model": "gpt-5",
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
        Assert.Equal("gpt-5", cost.Model);
        Assert.Equal(0.0024725, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void HfQwen3CoderNext_VerificationPlaceholder()
    {
        // Qwen3-Coder-480B-A35B-Instruct via Novita — rate not yet verified (2026-07-07).
        // Using the existing unverified entry (0.30/1.30). This test documents the
        // current rate and will break when it is updated, serving as a reminder.
        // uncached=1000, output=ceil(4/4)+50=51.
        // cost = (1000*0.30 + 51*1.30) / 1_000_000 = (300 + 66.3) / 1_000_000 = 0.0003663.
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
        Assert.Equal(0.0003663, cost.CostUsd, precision: 10);
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
