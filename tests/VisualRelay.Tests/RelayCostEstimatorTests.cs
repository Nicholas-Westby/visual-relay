using System.Text.Json;
using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayCostEstimatorTests
{
    // ── MoneyFormatter ──────────────────────────────────────────

    [Fact]
    public void Dollars_RoundsToNearestCent()
    {
        Assert.Equal("$0.30", MoneyFormatter.Dollars(0.304));
        Assert.Equal("$0.31", MoneyFormatter.Dollars(0.305));
        Assert.Equal("$1.23", MoneyFormatter.Dollars(1.234));
        Assert.Equal("$30.00", MoneyFormatter.Dollars(30));
    }

    [Fact]
    public void Dollars_ShowsNonZeroForSubCentAmounts()
    {
        Assert.Equal("$0.0005", MoneyFormatter.Dollars(0.0005));
        Assert.Equal("$0.00051", MoneyFormatter.Dollars(0.00051));
        Assert.Equal("$0.003", MoneyFormatter.Dollars(0.003));
        Assert.Equal("$0.0099", MoneyFormatter.Dollars(0.0099));
        Assert.Equal("$0.0000012", MoneyFormatter.Dollars(0.0000012));
    }

    [Fact]
    public void Dollars_ReservesZeroStringForZeroOrNegative()
    {
        Assert.Equal("$0.00", MoneyFormatter.Dollars(0));
        Assert.Equal("$0.00", MoneyFormatter.Dollars(-1.5));
    }

    [Fact]
    public void Dollars_KeepsTwoDecimalsAtAndAboveOneCent()
    {
        Assert.Equal("$0.01", MoneyFormatter.Dollars(0.01));
        Assert.Equal("$0.01", MoneyFormatter.Dollars(0.014));
        Assert.Equal("$0.02", MoneyFormatter.Dollars(0.015));
    }

    [Fact]
    public void Dollars_DoesNotThrowOnExtremelyTinyAmount()
    {
        Assert.StartsWith("$", MoneyFormatter.Dollars(1e-15));
        Assert.StartsWith("$", MoneyFormatter.Dollars(1e-10));
    }

    // ── Updated existing estimator tests ────────────────────────

    [Fact]
    public void EstimateReport_UsesCachedPromptTokensAndEstimatedOutput()
    {
        // uncached = 1500 (final context), output = ceil(12/4)+2*50 = 103.
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
                { "type": "llm_call", "prompt_tokens_est": 1500 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal("cheap-kimi", cost.Model);
        // PromptTokens now holds uncached input, not the sum of cumulative contexts.
        Assert.Equal(1_500, cost.PromptTokens);
        Assert.Equal(100, cost.CachedTokens);
        Assert.Equal(103, cost.OutputTokens);
        Assert.Equal(1.75, cost.DurationSeconds, precision: 2);
        Assert.True(cost.CostUsd > 0);
        Assert.Equal(0.00023912, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void EstimateReport_UsesUsdPerMillionTokenScale()
    {
        // uncached = 1500, output = ceil(400/4)+2*50 = 200.
        using var document = JsonDocument.Parse(
            $$"""
            {
              "model": "balanced-kimi",
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

        // PromptTokens is now uncached (1500), not the sum (2500).
        Assert.Equal(1_500, cost.PromptTokens);
        Assert.Equal(2_000, cost.CachedTokens);
        Assert.Equal(200, cost.OutputTokens);
        Assert.Equal(0.00083375, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void EstimateReport_UsesInputRateForCachedTokensWithoutCacheDiscount()
    {
        // Single turn: uncached = 1000, output = ceil(40/4)+50 = 60.
        using var document = JsonDocument.Parse(
            $$"""
            {
              "model": "vision",
              "result": { "answer": "{{new string('y', 40)}}" },
              "stats": { "prompt_cache": { "cached_tokens": 1000 } },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 1000 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.Equal(1_000, cost.PromptTokens);
        Assert.Equal(1_000, cost.CachedTokens);
        Assert.Equal(60, cost.OutputTokens);
        Assert.Equal(0.00069, cost.CostUsd, precision: 10);
    }

    // ── Core correctness regression tests ───────────────────────

    [Fact]
    public void EstimateReport_CumulativeTokenBug_IsGone()
    {
        // Old code summed cumulative prompt_tokens_est across turns (1000+3000+6000+10000=20000),
        // then subtracted also-cumulative cached_tokens (50000) → 0 uncached input.
        // Correct uncached = 1000+2000+3000+4000 = 10000 (final context).
        using var document = JsonDocument.Parse(
            """
            {
              "model": "balanced-kimi",
              "result": { "answer": "test" },
              "stats": {
                "prompt_cache": { "cached_tokens": 50000 }
              },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 1000 },
                { "type": "llm_call", "prompt_tokens_est": 3000 },
                { "type": "llm_call", "prompt_tokens_est": 6000 },
                { "type": "llm_call", "prompt_tokens_est": 10000 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        // Uncached input is 10000, not 0.
        Assert.Equal(10_000, cost.PromptTokens);
        Assert.True(cost.PromptTokens > 0, "uncached input must be > 0 for a multi-turn report");
        Assert.True(cost.CostUsd > 0.0004,
            $"expected cost > $0.0004 (has uncached input at $0.435/M), got {cost.CostUsd:F10}");
        Assert.Equal(0.00470612, cost.CostUsd, precision: 8);
    }

    [Fact]
    public void EstimateReport_EveryTokenClassContributes()
    {
        // uncached = 3000, cached = 1000, cache_write = 500, output = ceil(64/4)+3*50 = 166.
        // balanced-kimi rates: input $0.435, cached $0.003625, output $0.87.
        using var document = JsonDocument.Parse(
            """
            {
              "model": "balanced-kimi",
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
        Assert.Equal(3_000, cost.PromptTokens);
        Assert.Equal(1_000, cost.CachedTokens);
        Assert.Equal(500, cost.CacheWriteTokens);
        Assert.Equal(166, cost.OutputTokens);
        Assert.Equal(0.00167055, cost.CostUsd, precision: 6);

        // Sanity: removing any one class must strictly lower the total.
        var withoutUncached = (0 + 1000 * 0.003625 + 500 * 0.435 + 166 * 0.87) / 1_000_000d;
        var withoutCached = (3000 * 0.435 + 0 + 500 * 0.435 + 166 * 0.87) / 1_000_000d;
        var withoutCacheWrite = (3000 * 0.435 + 1000 * 0.003625 + 0 + 166 * 0.87) / 1_000_000d;
        var withoutOutput = (3000 * 0.435 + 1000 * 0.003625 + 500 * 0.435 + 0) / 1_000_000d;

        Assert.True(cost.CostUsd > withoutUncached, "removing uncached input should lower cost");
        Assert.True(cost.CostUsd > withoutCached, "removing cached read should lower cost");
        Assert.True(cost.CostUsd > withoutCacheWrite, "removing cache write should lower cost");
        Assert.True(cost.CostUsd > withoutOutput, "removing output should lower cost");
    }

    [Fact]
    public void EstimateReport_UnknownModel_ReturnsPricedFalse()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "model": "nonexistent-model-xyz",
              "result": { "answer": "hello" },
              "stats": { "prompt_cache": { "cached_tokens": 100 } },
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 500 }
              ]
            }
            """);

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.False(cost.Priced, "unknown model must return Priced=false");
        Assert.Equal("nonexistent-model-xyz", cost.Model);
        Assert.Equal(0d, cost.CostUsd);
    }

    // ── Turn-count tests ─────────────────────────────────────────

    [Fact]
    public void EstimateReport_SetsTurnsToLlmCallCount()
    {
        // 5 entries: 3 llm_call, 2 tool_call → Turns == 3.
        using var document = JsonDocument.Parse(
            """{"model":"cheap-kimi","result":{"answer":"ok"},"stats":{},"timeline":[{"type":"llm_call","prompt_tokens_est":100},{"type":"tool_call"},{"type":"llm_call","prompt_tokens_est":200},{"type":"tool_call"},{"type":"llm_call","prompt_tokens_est":300}]}""");

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.Equal(3, cost.Turns);
        Assert.True(cost.Priced);
    }

    [Fact]
    public void EstimateReport_UnknownModelStillCarriesTurnCount()
    {
        using var document = JsonDocument.Parse(
            """{"model":"nonexistent-model-xyz","result":{"answer":"hi"},"stats":{},"timeline":[{"type":"llm_call","prompt_tokens_est":100},{"type":"llm_call","prompt_tokens_est":200}]}""");

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.False(cost.Priced);
        Assert.Equal(2, cost.Turns);
    }

    [Fact]
    public void EstimateReport_EmptyTimeline_HasZeroTurns()
    {
        using var document = JsonDocument.Parse(
            """{"model":"cheap-kimi","result":{"answer":"x"},"stats":{},"timeline":[]}""");

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.Equal(0, cost.Turns);
        Assert.True(cost.Priced);
    }
}
