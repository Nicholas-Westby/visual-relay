using System.Globalization;
using System.Text.RegularExpressions;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed partial class CostPerModelTests
{
    // ── CachedInput / CacheWrite display ────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_CachedInputDisplay_NullFallsBackToInput()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var row = vm.ModelCostRows.Single(r => r.ModelKey == "hf-qwen3-coder-next");
        Assert.Equal("$0.3 per 1M tokens (same as input)", row.CachedInputDisplay);
    }

    [Fact]
    public void PopulateModelCostRows_CacheWriteDisplay_ExplicitEqualsInput_Annotated()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var row = vm.ModelCostRows.Single(r => r.ModelKey == "deepseek-v4-flash");
        Assert.EndsWith("(same as input)", row.CacheWriteDisplay, StringComparison.Ordinal);
        Assert.StartsWith("$0.14", row.CacheWriteDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void PopulateModelCostRows_CacheWriteDisplay_ExplicitDifferent_NoAnnotation()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var row = vm.ModelCostRows.Single(r => r.ModelKey == "claude-opus-1m");
        Assert.Equal("$6.25 per 1M tokens", row.CacheWriteDisplay);
        Assert.DoesNotContain("same as input", row.CacheWriteDisplay, StringComparison.Ordinal);
    }

    // ── Peak rows ───────────────────────────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_PeakCachedInput_UsesEffectiveNotZero()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var flash = vm.ModelCostRows.Single(r => r.ModelKey == "deepseek-v4-flash");
        var w = flash.Windows[0];
        Assert.StartsWith("$0.0056", w.PeakCachedInputDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void PopulateModelCostRows_PeakCacheWrite_AnnotatedWhenEqualsInput()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var flash = vm.ModelCostRows.Single(r => r.ModelKey == "deepseek-v4-flash");
        var w = flash.Windows[0];
        Assert.EndsWith("(same as input)", w.PeakCacheWriteDisplay, StringComparison.Ordinal);
    }

    // ── Culture safety ──────────────────────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_CultureSafe_RatesUseDotNotComma()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var vm = new MainWindowViewModel();
            vm.PopulateModelCostRows();
            var flash = vm.ModelCostRows.Single(r => r.ModelKey == "deepseek-v4-flash");
            Assert.Contains(".", flash.InputDisplay, StringComparison.Ordinal);
            Assert.DoesNotContain(",", flash.InputDisplay, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    // ── Window headline and source note ─────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_WindowHeadline_Matches12HourPattern()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var flash = vm.ModelCostRows.Single(r => r.ModelKey == "deepseek-v4-flash");
        var w = flash.Windows[0];
        Assert.Matches(
            @"^\d{1,2}:\d{2} [AP]M – \d{1,2}:\d{2} [AP]M your time — 2× peak pricing$",
            w.Headline);
    }

    [Fact]
    public void PopulateModelCostRows_WindowSourceNote_IsCorrect()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var flash = vm.ModelCostRows.Single(r => r.ModelKey == "deepseek-v4-flash");
        var w = flash.Windows[0];
        Assert.Equal("(9:00 AM – 12:00 PM in Asia/Shanghai)", w.SourceNote);
    }

    [Fact]
    public void PopulateModelCostRows_WindowHeadline_NeverContainsPlatformTimezoneId()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var flash = vm.ModelCostRows.Single(r => r.ModelKey == "deepseek-v4-flash");
        foreach (var w in flash.Windows)
        {
            Assert.DoesNotContain(TimeZoneInfo.Local.Id, w.Headline, StringComparison.Ordinal);
            Assert.DoesNotContain("PST8PDT", w.Headline, StringComparison.Ordinal);
        }
    }
}
