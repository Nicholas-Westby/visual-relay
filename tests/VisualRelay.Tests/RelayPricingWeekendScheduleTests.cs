using System.Text.Json;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

/// <summary>
/// DeepSeek's peak schedule runs Monday through Friday only
/// (api-docs.deepseek.com/quick_start/pricing, 2026-08-31: "Peak hours are
/// 01:00 - 04:00 and 06:00 - 10:00 UTC, Monday through Friday"). A window that
/// fires every day charges 2x for seven hours of every Saturday and Sunday that
/// DeepSeek bills at the base rate, so weekend runs read as twice their real
/// cost. The day is read in the window's own timezone, since Asia/Shanghai is
/// where the schedule is defined.
/// </summary>
public sealed class RelayPricingWeekendScheduleTests
{
    /// <summary>Same token stats as the weekday peak cases in
    /// <c>RelayPricingScheduleTests</c>: uncached=1500, cached=100, output=103.
    /// Base cost 0.00039868, doubled to 0.00079736 inside a peak window.</summary>
    private static JsonDocument ReportAt(string timestamp) => JsonDocument.Parse(
        $$"""
        {
          "model": "cheap",
          "timestamp": "{{timestamp}}",
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

    [Fact]
    public void CheapModel_SaturdayInsidePeakHours_UsesBaseRates()
    {
        // 2026-07-18T01:00:00Z = Saturday 09:00 Asia/Shanghai — the first peak
        // window's exact start time, on a day the schedule does not cover.
        using var document = ReportAt("2026-07-18T01:00:00Z");

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal(0.00039868, cost.CostUsd, precision: 10);
    }

    [Fact]
    public void CheapModel_SundayInsidePeakHours_UsesBaseRates()
    {
        // 2026-07-19T01:00:00Z = Sunday 09:00 Asia/Shanghai.
        using var document = ReportAt("2026-07-19T01:00:00Z");

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal(0.00039868, cost.CostUsd, precision: 10);
    }

    /// <summary>
    /// The Monday either side of that weekend still doubles, so the fix narrows
    /// the schedule to the published days rather than switching it off.
    /// </summary>
    [Fact]
    public void CheapModel_MondayInsidePeakHours_StillDoublesRates()
    {
        // 2026-07-20T01:00:00Z = Monday 09:00 Asia/Shanghai.
        using var document = ReportAt("2026-07-20T01:00:00Z");

        var cost = RelayCostEstimator.EstimateReport(document.RootElement);

        Assert.True(cost.Priced);
        Assert.Equal(0.00079736, cost.CostUsd, precision: 10);
    }

    /// <summary>
    /// A window with no day restriction keeps firing every day. Only DeepSeek
    /// publishes a weekday-only schedule today, so the day list has to be
    /// opt-in rather than a new requirement on every future window.
    /// </summary>
    [Fact]
    public void WindowWithoutDayRestriction_MatchesOnAWeekend()
    {
        var everyDay = new RateWindow(new(9, 0), new(12, 0), "Asia/Shanghai", 2.0);
        var weekdaysOnly = everyDay with { Days = RateWindow.Weekdays };

        Assert.True(everyDay.MatchesDay(DayOfWeek.Saturday));
        Assert.True(everyDay.MatchesDay(DayOfWeek.Monday));
        Assert.False(weekdaysOnly.MatchesDay(DayOfWeek.Saturday));
        Assert.True(weekdaysOnly.MatchesDay(DayOfWeek.Monday));
    }
}
