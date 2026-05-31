using System.Text.Json;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

public sealed class RelayCostEstimatorTests
{
    [Fact]
    public void EstimateReport_UsesCachedPromptTokensAndEstimatedOutput()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "model": "cheap-kimi",
              "result": { "answer": "abcdefghijkl" },
              "stats": {
                "total_llm_time_s": 1.5,
                "total_tool_time_s": 0.25,
                "prompt_cache": { "cached_tokens": 100 }
              },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 1000 },
                { "type": "tool_call", "prompt_tokens_est": 9999 },
                { "type": "llm_call", "prompt_tokens_est": 500 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("cheap-kimi", cost.Model);
        Assert.Equal(1_500, cost.PromptTokens);
        Assert.Equal(100, cost.CachedTokens);
        Assert.Equal(103, cost.OutputTokens);
        Assert.Equal(1.75, cost.DurationSeconds, precision: 2);
        Assert.True(cost.CostUsd > 0);
    }
}
