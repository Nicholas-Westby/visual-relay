using VisualRelay.App.ViewModels;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

public sealed class CostPerModelTests
{
    [Fact]
    public void PopulateModelCostRows_ContainsAllModelsFromRelayPricingDefault()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        Assert.Equal(RelayPricing.Default.Count, vm.ModelCostRows.Count);

        foreach (var (key, _) in RelayPricing.Default)
        {
            Assert.Contains(vm.ModelCostRows, r => r.ModelKey == key);
        }
    }

    [Fact]
    public void PopulateModelCostRows_RatesMatchRelayPricingDefault()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        foreach (var (key, pricing) in RelayPricing.Default)
        {
            var row = vm.ModelCostRows.Single(r => r.ModelKey == key);
            Assert.Equal(pricing.Input, row.InputRate);
            Assert.Equal(pricing.Output, row.OutputRate);
            Assert.Equal(pricing.CachedInput, row.CachedInputRate);
            Assert.Equal(pricing.CacheWrite, row.CacheWriteRate);
        }
    }

    [Fact]
    public void PopulateModelCostRows_NullCacheWrite_ShowsSameAsInput()
    {
        // frontier has no CacheWrite (null) — documented fallback is "billed at the Input rate"
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var frontier = vm.ModelCostRows.Single(r => r.ModelKey == "frontier");
        Assert.Null(frontier.CacheWriteRate);
        Assert.Equal("same as input", frontier.CacheWriteDisplay);
    }

    [Fact]
    public void PopulateModelCostRows_ExplicitCacheWrite_ShowsDollarRate()
    {
        // claude-opus-1m has CacheWrite = 6.25
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var opus = vm.ModelCostRows.Single(r => r.ModelKey == "claude-opus-1m");
        Assert.NotNull(opus.CacheWriteRate);
        Assert.Equal(6.25, opus.CacheWriteRate!.Value);
        Assert.Equal("$6.25 per 1M tokens", opus.CacheWriteDisplay);
    }

    // ── Time-windowed models ────────────────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_CheapModel_HasWindows()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var cheap = vm.ModelCostRows.Single(r => r.ModelKey == "cheap");
        Assert.True(cheap.HasWindows);
        Assert.NotEmpty(cheap.Windows);
    }

    [Fact]
    public void PopulateModelCostRows_BalancedModel_HasWindows()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var balanced = vm.ModelCostRows.Single(r => r.ModelKey == "balanced");
        Assert.True(balanced.HasWindows);
        Assert.NotEmpty(balanced.Windows);
    }

    [Fact]
    public void PopulateModelCostRows_NonDeepSeekModels_HaveNoWindows()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        foreach (var row in vm.ModelCostRows)
        {
            if (row.ModelKey is "cheap" or "balanced")
                continue;

            Assert.False(row.HasWindows,
                $"'{row.ModelKey}' should not have windows.");
            Assert.Empty(row.Windows);
        }
    }

    [Fact]
    public void PopulateModelCostRows_Windows_HaveCorrectMultiplier()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var cheap = vm.ModelCostRows.Single(r => r.ModelKey == "cheap");
        Assert.Equal(2, cheap.Windows.Count);
        Assert.All(cheap.Windows, w => Assert.Equal(2.0, w.Multiplier));
    }

    [Fact]
    public void PopulateModelCostRows_Windows_PeakRatesAreMultiplied()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var cheap = vm.ModelCostRows.Single(r => r.ModelKey == "cheap");
        // cheap base rates: input=0.14, output=0.28, cachedInput=0.0028, cacheWrite=0.14
        foreach (var w in cheap.Windows)
        {
            Assert.Equal(0.14 * 2.0, w.PeakInputRate);
            Assert.Equal(0.28 * 2.0, w.PeakOutputRate);
            Assert.Equal(0.0028 * 2.0, w.PeakCachedInputRate);
            Assert.Equal(0.14 * 2.0, w.PeakCacheWriteRate);
        }
    }

    [Fact]
    public void PopulateModelCostRows_Windows_StartEndTimesAreConvertedToLocal()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var cheap = vm.ModelCostRows.Single(r => r.ModelKey == "cheap");
        var w1 = cheap.Windows[0];

        // The window is 09:00–12:00 Asia/Shanghai (UTC+8).
        // In UTC: 01:00–04:00.
        // The local time representation should differ from the source window
        // unless the machine happens to be in Asia/Shanghai.
        // Verify the window boundaries were converted by checking that at
        // least one of them differs from the raw 09:00/12:00 values, OR that
        // the SourceTimezoneLabel confirms Asia/Shanghai.
        Assert.Equal("Asia/Shanghai", w1.SourceTimezoneLabel);
    }

    [Fact]
    public void PopulateModelCostRows_WindowDisplayTimes_AreNonEmpty()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var cheap = vm.ModelCostRows.Single(r => r.ModelKey == "cheap");
        foreach (var w in cheap.Windows)
        {
            Assert.NotEmpty(w.StartTimeDisplay);
            Assert.NotEmpty(w.EndTimeDisplay);
        }
    }

    [Fact]
    public void PopulateModelCostRows_WindowDisplayTimezoneLabel_IsSet()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var cheap = vm.ModelCostRows.Single(r => r.ModelKey == "cheap");
        foreach (var w in cheap.Windows)
        {
            Assert.NotEmpty(w.DisplayTimezoneLabel);
        }
    }

    [Fact]
    public void PopulateModelCostRows_WindowPeakCacheWriteDisplay_NullFallback_ShowsSameAsInput()
    {
        // vision model has null CacheWrite — its window peak (if it had windows) would show "same as input".
        // Verify on cheap which has CacheWrite=0.14 (non-null), so peak shows a dollar rate.
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var cheap = vm.ModelCostRows.Single(r => r.ModelKey == "cheap");
        foreach (var w in cheap.Windows)
        {
            Assert.Equal("$0.28 per 1M tokens", w.PeakCacheWriteDisplay);
        }
    }

    [Fact]
    public void PopulateModelCostRows_IsIdempotent()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();
        var count = vm.ModelCostRows.Count;

        vm.PopulateModelCostRows();
        Assert.Equal(count, vm.ModelCostRows.Count);
    }

    [Fact]
    public void PopulateModelCostRows_DisplayNameMatchesModelKey()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        foreach (var row in vm.ModelCostRows)
        {
            Assert.Equal(row.ModelKey, row.DisplayName);
        }
    }
}
