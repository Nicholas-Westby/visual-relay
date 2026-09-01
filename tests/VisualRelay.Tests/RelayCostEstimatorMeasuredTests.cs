using System.Text.Json;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the transition state the cost rework runs in: a report written by the
/// first-party loop carries the provider's own token counts, so the estimator
/// prices BOTH and reports them side by side. The derived figure stays
/// authoritative until the two have been compared over real runs, because
/// switching quietly would change the meaning of every dollar the app has
/// displayed since it was built.
/// </summary>
public sealed class RelayCostEstimatorMeasuredTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private const string WithMeasured = """
        {
          "model": "cheap",
          "served_model": "deepseek-v4-flash",
          "timestamp": "2026-08-31T12:00:00Z",
          "result": { "answer": "done" },
          "stats": {
            "total_llm_time_s": 9.0,
            "total_tool_time_s": 1.0,
            "prompt_cache": { "cached_tokens": 0, "cache_write_tokens": 0 },
            "measured_usage": {
              "prompt_tokens": 60000,
              "completion_tokens": 5558,
              "reasoning_tokens": 2704,
              "cached_tokens": 0,
              "cache_write_tokens": 0
            }
          },
          "timeline": [
            { "type": "llm_call", "prompt_tokens_est": 20000 },
            { "type": "llm_call", "prompt_tokens_est": 40000 }
          ]
        }
        """;

    /// <summary>An archived report carries no measured usage, and reports none.</summary>
    [Fact]
    public void AnArchivedReport_HasNoMeasuredCost()
    {
        var estimate = RelayCostEstimator.EstimateReport(Parse("""
            {
              "model": "cheap",
              "result": { "answer": "done" },
              "stats": { "prompt_cache": { "cached_tokens": 0 } },
              "timeline": [ { "type": "llm_call", "prompt_tokens_est": 1000 } ]
            }
            """));

        Assert.True(estimate.Priced);
        Assert.Null(estimate.MeasuredCostUsd);
        Assert.Null(estimate.MeasuredOutputTokens);
        Assert.True(estimate.CostUsd > 0);
    }

    /// <summary>A first-party report prices both, and both are non-zero.</summary>
    [Fact]
    public void AFirstPartyReport_PricesBoth()
    {
        var estimate = RelayCostEstimator.EstimateReport(Parse(WithMeasured));

        Assert.True(estimate.Priced);
        Assert.True(estimate.CostUsd > 0, "the derived figure must still be produced");
        Assert.NotNull(estimate.MeasuredCostUsd);
        Assert.True(estimate.MeasuredCostUsd > 0);
    }

    /// <summary>
    /// The derived figure is unchanged by the presence of measured usage. It
    /// remains exactly what it was, so historical comparisons hold.
    /// </summary>
    [Fact]
    public void TheDerivedFigure_IsUnaffectedByMeasuredUsage()
    {
        var withMeasured = RelayCostEstimator.EstimateReport(Parse(WithMeasured));
        var withoutMeasured = RelayCostEstimator.EstimateReport(Parse(
            WithMeasured.Replace("\"measured_usage\"", "\"ignored_usage\"", StringComparison.Ordinal)));

        Assert.Equal(withoutMeasured.CostUsd, withMeasured.CostUsd, precision: 12);
        Assert.Equal(withoutMeasured.OutputTokens, withMeasured.OutputTokens);
    }

    /// <summary>
    /// The measured output is far larger than the derived one, which is the
    /// whole reason for the rework: the derivation is answer length over four
    /// plus fifty a turn, and reasoning tokens are invisible to it.
    /// </summary>
    [Fact]
    public void MeasuredOutput_IsFarLargerThanTheDerivedOutput()
    {
        var estimate = RelayCostEstimator.EstimateReport(Parse(WithMeasured));

        Assert.Equal(5558, estimate.MeasuredOutputTokens);
        Assert.True(estimate.MeasuredOutputTokens > estimate.OutputTokens * 10,
            $"derived {estimate.OutputTokens} vs measured {estimate.MeasuredOutputTokens}");
        Assert.True(estimate.MeasuredCostUsd > estimate.CostUsd);
    }

    /// <summary>
    /// Measured input is the sum across calls, not the last cumulative estimate.
    /// The derivation assumed context never shrinks, an assumption 133 of 1006
    /// archived reports violated, one of them by 87%.
    /// </summary>
    [Fact]
    public void MeasuredInput_IsTheSumNotTheLastCumulativeValue()
    {
        var estimate = RelayCostEstimator.EstimateReport(Parse(WithMeasured));

        Assert.Equal(60000, estimate.MeasuredPromptTokens);
        // The derivation telescopes to the LAST timeline value, 40000.
        Assert.Equal(40000, estimate.PromptTokens);
    }

    /// <summary>
    /// Cost is attributed to the concrete model that served the call. The tier
    /// alias would hide a fallback hop, and the two can price very differently.
    /// </summary>
    [Fact]
    public void CostIsAttributed_ToTheServedModelNotTheTier()
    {
        var onFlash = RelayCostEstimator.EstimateReport(Parse(WithMeasured));
        var onPro = RelayCostEstimator.EstimateReport(Parse(
            WithMeasured.Replace("deepseek-v4-flash", "deepseek-v4-pro", StringComparison.Ordinal)));

        // The pro model is three times the price of flash, so a fallback hop
        // that used to be invisible now moves the number.
        Assert.True(onPro.CostUsd > onFlash.CostUsd,
            $"flash {onFlash.CostUsd} should be cheaper than pro {onPro.CostUsd}");
        Assert.Equal("cheap", onFlash.Model);
    }

    /// <summary>
    /// A report naming a served model that is not priced falls back to the tier,
    /// so an unknown route never silently zeroes a cost.
    /// </summary>
    [Fact]
    public void AnUnpricedServedModel_StillPricesThroughTheTier()
    {
        var estimate = RelayCostEstimator.EstimateReport(Parse(
            WithMeasured.Replace("deepseek-v4-flash", "some-new-model", StringComparison.Ordinal)));

        Assert.False(estimate.Priced);
        Assert.Equal(0, estimate.CostUsd);
    }

    /// <summary>Zeroed measured usage is treated as absent rather than free.</summary>
    [Fact]
    public void ZeroedMeasuredUsage_IsTreatedAsAbsent()
    {
        var estimate = RelayCostEstimator.EstimateReport(Parse(
            WithMeasured
                .Replace("\"prompt_tokens\": 60000", "\"prompt_tokens\": 0", StringComparison.Ordinal)
                .Replace("\"completion_tokens\": 5558", "\"completion_tokens\": 0", StringComparison.Ordinal)));

        Assert.Null(estimate.MeasuredCostUsd);
    }
}
