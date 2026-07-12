using System.Text.Json;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

public sealed partial class RelayCostEstimatorTests
{
    // ── Tier-alias resolution ──────────────────────────────────────

    [Fact]
    public void EstimateReport_TierAliasCheap_MatchesConcreteDeepseekV4Flash()
    {
        // Identical token stats, model "cheap" vs "deepseek-v4-flash" → same cost.
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
              "model": "deepseek-v4-flash",
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
        Assert.Equal("deepseek-v4-flash", concreteCost.Model);
        Assert.Equal(cheapCost.CostUsd, concreteCost.CostUsd);
        Assert.Equal(0.00023912, cheapCost.CostUsd, precision: 10);
    }

    [Fact]
    public void EstimateReport_TierAliasFrontier_PricesAtGlm52Rates()
    {
        // "frontier" resolves to "glm-5.2": input 1.40, cached 0.26, output 4.40.
        // uncached=2000, cached=500, output=ceil(40/4)+50=60.
        // cost = (2000*1.40 + 500*0.26 + 60*4.40) / 1_000_000 = 0.003194.
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
        Assert.Equal(0.003194, cost.CostUsd, precision: 10);
    }
}
