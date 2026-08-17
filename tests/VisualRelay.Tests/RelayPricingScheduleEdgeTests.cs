using System.Globalization;
using System.Text.Json;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

public sealed class RelayPricingScheduleEdgeTests
{
    [Fact]
    public void UnknownModel_WithPeakTimestamp_StillReturnsPricedFalse()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "model": "nonexistent-model-xyz",
              "timestamp": "2026-07-15T01:00:00Z",
              "result": { "answer": "hello" },
              "stats": { "prompt_cache": { "cached_tokens": 100 } },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 500 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.False(cost.Priced, "unknown model must return Priced=false even with a timestamp");
        Assert.Equal("nonexistent-model-xyz", cost.Model);
        Assert.Equal(0d, cost.CostUsd);
    }

    [Fact]
    public void MissingTimestamp_DefaultsToBaseRates()
    {
        // No "timestamp" field → evaluationInstant falls back to DateTime.MinValue →
        // no window matches → multiplier 1×.
        using var document = JsonDocument.Parse(
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

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal(0.00039868, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void ExplicitEvaluationInstant_OverridesReportTimestamp()
    {
        // Report timestamp is in peak window, but explicit parameter is off-peak.
        using var document = JsonDocument.Parse(
            """
            {
              "model": "cheap",
              "timestamp": "2026-07-15T01:00:00Z",
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

        var offPeak = DateTime.Parse("2026-07-15T00:00:00Z", null, DateTimeStyles.AdjustToUniversal);
        var cost = RelayCostEstimator.EstimateReport(document.RootElement, offPeak);

        Assert.True(cost.Priced);
        Assert.Equal(0.00039868, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void ExplicitEvaluationInstant_PeakOverridesOffPeakReport()
    {
        // Report timestamp is off-peak, but explicit parameter is in peak window.
        using var document = JsonDocument.Parse(
            """
            {
              "model": "cheap",
              "timestamp": "2026-07-15T00:00:00Z",
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

        var peak = DateTime.Parse("2026-07-15T06:00:00Z", null, DateTimeStyles.AdjustToUniversal);
        var cost = RelayCostEstimator.EstimateReport(document.RootElement, peak);

        Assert.True(cost.Priced);
        Assert.Equal(0.00079736, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void CheapModel_PeakMultipliesCacheWriteTokens()
    {
        // uncached=3000, cached=1000, cache-write=500, output=ceil(64/4)+3*50=166.
        // cheap rates: input 0.22, cached 0.007, cache-write 0.22, output 0.66.
        // base = (3000*0.22 + 1000*0.007 + 500*0.22 + 166*0.66) / 1_000_000 = 0.00088656.
        // peak = 2× = 0.00177312.
        using var document = JsonDocument.Parse(
            """
            {
              "model": "cheap",
              "timestamp": "2026-07-15T01:00:00Z",
              "result": { "answer": "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!?" },
              "stats": {
                "prompt_cache": { "cached_tokens": 1000, "cache_write_tokens": 500 }
              },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 1000 },
                { "type": "llm_call", "prompt_tokens_est": 2000 },
                { "type": "llm_call", "prompt_tokens_est": 3000 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal(500, cost.CacheWriteTokens);
        Assert.Equal(0.00177312, cost.CostUsd, precision: 10);
    }
}
