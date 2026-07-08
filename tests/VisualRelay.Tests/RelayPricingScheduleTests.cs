using System.Globalization;
using System.Text.Json;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

public sealed class RelayPricingScheduleTests
{
    // ── Peak window: cheap (DeepSeek) model ──────────────────────

    [Fact]
    public void CheapModel_InsidePeakWindow_DoublesAllRates()
    {
        // 2026-07-15T01:00:00Z = 09:00 Asia/Shanghai — first peak window start (inclusive).
        // uncached=1500, cached=100, output=ceil(12/4)+2*50=103.
        // base cost = 0.00023912; peak = 2× = 0.00047824.
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

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("cheap", cost.Model);
        Assert.Equal(0.00047824, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void CheapModel_OffPeak_UsesBaseRates()
    {
        // 2026-07-15T00:00:00Z = 08:00 Asia/Shanghai — before first peak window.
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

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal(0.00023912, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void CheapModel_BetweenPeakWindows_UsesBaseRates()
    {
        // 2026-07-15T04:01:00Z = 12:01 Asia/Shanghai — between the two peak windows.
        using var document = JsonDocument.Parse(
            """
            {
              "model": "cheap",
              "timestamp": "2026-07-15T04:01:00Z",
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
        Assert.Equal(0.00023912, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void CheapModel_EndBoundaryIsExclusive()
    {
        // 2026-07-15T04:00:00Z = 12:00 Asia/Shanghai — EndLocal of first window (exclusive).
        using var document = JsonDocument.Parse(
            """
            {
              "model": "cheap",
              "timestamp": "2026-07-15T04:00:00Z",
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
        Assert.Equal(0.00023912, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void CheapModel_SecondWindowEndBoundaryIsExclusive()
    {
        // 2026-07-15T10:00:00Z = 18:00 Asia/Shanghai — EndLocal of second window (exclusive).
        using var document = JsonDocument.Parse(
            """
            {
              "model": "cheap",
              "timestamp": "2026-07-15T10:00:00Z",
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
        Assert.Equal(0.00023912, cost.CostUsd, precision: 10);
    }

    // ── Peak window: balanced (DeepSeek) model ───────────────────

    [Fact]
    public void BalancedModel_InsidePeakWindow_DoublesRates()
    {
        // 2026-07-15T06:00:00Z = 14:00 Asia/Shanghai — second peak window start (inclusive).
        // Same token counts as the existing balanced test: uncached=1500, cached=2000, output=200.
        // base cost = 0.00083375; peak = 2× = 0.0016675.
        using var document = JsonDocument.Parse(
            $$"""
            {
              "model": "balanced",
              "timestamp": "2026-07-15T06:00:00Z",
              "result": { "answer": "{{new string('x', 400)}}" },
              "stats": { "prompt_cache": { "cached_tokens": 2000 } },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 1000 },
                { "type": "llm_call", "prompt_tokens_est": 1500 },
                { "type": "tool_call", "prompt_tokens_est": 99999 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal(1_500, cost.PromptTokens);
        Assert.Equal(2_000, cost.CachedTokens);
        Assert.Equal(200, cost.OutputTokens);
        Assert.Equal(0.0016675, cost.CostUsd, precision: 10);
    }

    // ── Non-DeepSeek model unaffected by peak windows ────────────

    [Fact]
    public void ClaudeSonnet_InsideDeepSeekPeak_Unaffected()
    {
        // Claude Sonnet has no Windows — peak timestamp must not affect its rates.
        // uncached=1500, cached=100, output=ceil(12/4)+2*50=103.
        // rates: input 3.0, cached 0.30, output 15.0.
        // cost = (1500*3.0 + 100*0.30 + 103*15.0) / 1_000_000 = 0.006075.
        using var document = JsonDocument.Parse(
            """
            {
              "model": "claude-sonnet",
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

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("claude-sonnet", cost.Model);
        Assert.Equal(0.006075, cost.CostUsd, precision: 10);
    }
}
