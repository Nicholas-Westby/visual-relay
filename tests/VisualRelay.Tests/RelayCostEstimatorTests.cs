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
        var result15 = MoneyFormatter.Dollars(1e-15);
        Assert.NotNull(result15);
        Assert.NotEmpty(result15);
        Assert.StartsWith("$", result15);

        var result10 = MoneyFormatter.Dollars(1e-10);
        Assert.NotNull(result10);
        Assert.StartsWith("$", result10);
    }

    // ── Updated existing estimator tests ────────────────────────

    [Fact]
    public void EstimateReport_UsesCachedPromptTokensAndEstimatedOutput()
    {
        // Fixture with two monotonic cumulative turns.
        // uncached = 1000 + max(0, 1500-1000) = 1500 (telescopes to final context).
        // output  = ceil(12/4) + 2*50 = 3 + 100 = 103.
        // cost = (1500*0.14 + 100*0.0028 + 103*0.28)/1e6 = 239.12/1e6 = 0.00023912.
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
        // Two monotonic cumulative turns.
        // uncached = 1000 + max(0, 1500-1000) = 1500.
        // output  = ceil(400/4) + 2*50 = 100 + 100 = 200.
        // cost = (1500*0.435 + 2000*0.003625 + 200*0.87)/1e6
        //      = (652.5 + 7.25 + 174)/1e6 = 833.75/1e6 = 0.00083375.
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
        // Single turn — uncached = 1000.
        // output = ceil(40/4) + 1*50 = 10 + 50 = 60.
        // cost = (1000*0.30 + 1000*0.30 + 60*1.50)/1e6 = 690/1e6 = 0.00069.
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
        // The old code sums cumulative prompt_tokens_est across turns
        // (1000+3000+6000+10000=20000) then subtracts also-cumulative
        // cached_tokens (50000) → max(0, 20000-50000) = 0 uncached input.
        //
        // The correct per-turn incremental model:
        //   uncached = 1000 + max(0,3000-1000) + max(0,6000-3000) + max(0,10000-6000)
        //            = 1000 + 2000 + 3000 + 4000 = 10000 (= final context).
        //
        // This test proves the cumulative-zero-floor bug is gone: uncached > 0
        // and the cost includes the input-rate charge.
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
        // Cost must materially exceed cached+output-only cost.
        // cached+output-only = (50000*0.003625 + 201*0.87)/1e6 ≈ 0.00035612
        // full cost            = (10000*0.435 + 50000*0.003625 + 201*0.87)/1e6 ≈ 0.00470612
        Assert.True(cost.CostUsd > 0.0004,
            $"expected cost > $0.0004 (has uncached input at $0.435/M), got {cost.CostUsd:F10}");
        Assert.Equal(0.00470612, cost.CostUsd, precision: 8);
    }

    [Fact]
    public void EstimateReport_EveryTokenClassContributes()
    {
        // Synthetic report exercising all four token classes.
        //
        // uncached  = final context = 3000  (1000 + 1000 + 1000 incremental)
        // cached    = 1000
        // cache_write = 500
        // output    = ceil(64/4) + 3*50 = 16 + 150 = 166
        //
        // balanced-kimi rates: input $0.435, cached $0.003625, output $0.87.
        // cache-write rate falls back to input rate ($0.435) when not specified.
        //
        // cost = (3000*0.435 + 1000*0.003625 + 500*0.435 + 166*0.87)/1e6
        //      = (1305 + 3.625 + 217.5 + 144.42)/1e6
        //      = 1670.545/1e6 = 0.001670545
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
        // cost = (3000*0.435 + 1000*0.003625 + 500*0.435 + 166*0.87) / 1_000_000
        //      = 1670.545 / 1_000_000 = 0.001670545; 6 decimal places guards
        //      against floating-point rounding on the last digit.
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
        // An unknown model must not silently report $0 — Priced must be false
        // so the caller can surface an "unknown" badge rather than a false $0.
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
}
