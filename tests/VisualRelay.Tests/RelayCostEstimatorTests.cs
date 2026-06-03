using System.Text.Json;
using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayCostEstimatorTests
{
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
        // Sub-cent amounts must not collapse to "$0.00": keep enough
        // significant digits (2 sig figs) that real spend stays visible.
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
        // Sub-cent branch computes decimals = SubCentSignificantFigures - 1 - magnitude.
        // For 1e-15 that yields 16, but Math.Round only accepts 0–15 digits.
        // The formatter must clamp and not throw ArgumentOutOfRangeException.
        var result15 = MoneyFormatter.Dollars(1e-15);
        Assert.NotNull(result15);
        Assert.NotEmpty(result15);
        Assert.StartsWith("$", result15);

        var result10 = MoneyFormatter.Dollars(1e-10);
        Assert.NotNull(result10);
        Assert.StartsWith("$", result10);
    }

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

    [Fact]
    public void EstimateReport_UsesUsdPerMillionTokenScale()
    {
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

        Assert.Equal(2_500, cost.PromptTokens);
        Assert.Equal(2_000, cost.CachedTokens);
        Assert.Equal(200, cost.OutputTokens);
        Assert.Equal(0.00039875, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void EstimateReport_UsesInputRateForCachedTokensWithoutCacheDiscount()
    {
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
        Assert.Equal(0.00039, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void TaskRunMetric_SumsAndFormatsUsdWithoutCentConversion()
    {
        var metric = new TaskRunMetric(
            "alpha",
            [
                Stage(1, 0.30),
                Stage(2, 1.20)
            ]);

        Assert.Equal(1.50, metric.CostUsd, precision: 2);
        Assert.Equal("$1.50", metric.CostLabel);
        Assert.Equal("2 steps  3s  $1.50", metric.SummaryLabel);
    }

    private static StageRunMetric Stage(int number, double costUsd) =>
        new(number, $"Stage {number}", "balanced", "balanced-kimi", DateTimeOffset.UtcNow, 1.5, costUsd, true, 0, 0, 0, "/tmp/report.json", null);
}
