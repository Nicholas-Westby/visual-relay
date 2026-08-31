using System.Text.Json;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

public sealed partial class RelayCostEstimatorTests
{
    // ── Tier-alias resolution ──────────────────────────────────────

    [Fact]
    public void EstimateReport_TierAliasCheap_MatchesConcreteDeepseekV4FlashVisionExp()
    {
        // Identical token stats, model "cheap" vs the concrete model the cheap
        // alias resolves to → same cost.
        using var cheapDoc = JsonDocument.Parse(
            """
            {
              "model": "cheap",
              "result": { "answer": "abcdefghijkl" },
              "stats": {
                "total_llm_time_s": 1.5,
                "total_tool_time_s": 0.25,
                "prompt_cache": { "cached_tokens": 100 }
              },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 1000 },
                { "type": "tool_call", "prompt_tokens_est": 9999 },
                { "type": "llm_call", "prompt_tokens_est": 1500 }
              ]
            }
            """);

        using var concreteDoc = JsonDocument.Parse(
            """
            {
              "model": "deepseek-v4-flash-vision-exp",
              "result": { "answer": "abcdefghijkl" },
              "stats": {
                "total_llm_time_s": 1.5,
                "total_tool_time_s": 0.25,
                "prompt_cache": { "cached_tokens": 100 }
              },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 1000 },
                { "type": "tool_call", "prompt_tokens_est": 9999 },
                { "type": "llm_call", "prompt_tokens_est": 1500 }
              ]
            }
            """);

        var cheapCost = RelayCostEstimator.EstimateReport(cheapDoc.RootElement);
        var concreteCost = RelayCostEstimator.EstimateReport(concreteDoc.RootElement);

        Assert.True(cheapCost.Priced);
        Assert.True(concreteCost.Priced);
        Assert.Equal("cheap", cheapCost.Model);
        Assert.Equal("deepseek-v4-flash-vision-exp", concreteCost.Model);
        Assert.Equal(cheapCost.CostUsd, concreteCost.CostUsd);
        Assert.Equal(0.00039868, cheapCost.CostUsd, precision: 10);
    }

    [Fact]
    public void EstimateReport_TierAliasFrontier_PricesAtGlmRates()
    {
        // "frontier" resolves to "glm-5.3-flash": input 0.15, cached 0.03, output 0.50.
        // uncached=2000, cached=500, output=ceil(40/4)+50=60.
        // cost = (2000*0.15 + 500*0.03 + 60*0.50) / 1_000_000
        //      = (300 + 15 + 30) / 1_000_000 = 345 / 1_000_000 = 0.000345.
        using var document = JsonDocument.Parse(
            $$"""
            {
              "model": "frontier",
              "result": { "answer": "{{new string('y', 40)}}" },
              "stats": { "prompt_cache": { "cached_tokens": 500 } },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 2000 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("frontier", cost.Model);
        Assert.Equal(0.000345, cost.CostUsd, precision: 10);
    }
}
