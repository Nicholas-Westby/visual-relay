using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed partial class CostPerModelTests
{
    // ── No tier aliases as card keys ────────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_NoModelKeyIsATierAlias()
    {
        var tierAliases = new HashSet<string>(
            BackendConfigGenerator.DefaultTierResolution.Keys, StringComparer.Ordinal);

        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        foreach (var row in vm.ModelCostRows)
            Assert.DoesNotContain(row.ModelKey, tierAliases);
    }

    [Fact]
    public void PopulateModelCostRows_NoDuplicateModelKeys()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var keys = vm.ModelCostRows.Select(r => r.ModelKey).ToList();
        Assert.Equal(keys.Distinct(StringComparer.Ordinal).Count(), keys.Count);
    }

    // ── Default resolution badges ───────────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_DefaultBadges()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var cheapCard = vm.ModelCostRows.First(r => r.TierBadges.Contains("cheap"));
        Assert.Equal("deepseek-v4-flash", cheapCard.ModelKey);
        Assert.True(cheapCard.IsActive);

        var balancedCard = vm.ModelCostRows.First(r => r.TierBadges.Contains("balanced"));
        Assert.Equal("deepseek-v4-pro", balancedCard.ModelKey);
        Assert.True(balancedCard.IsActive);

        var frontierCard = vm.ModelCostRows.First(r => r.TierBadges.Contains("frontier"));
        Assert.Equal("glm-5.2", frontierCard.ModelKey);
        Assert.True(frontierCard.IsActive);

        var visionCard = vm.ModelCostRows.First(r => r.TierBadges.Contains("vision"));
        Assert.Equal("hf-qwen3-vl-235b", visionCard.ModelKey);
        Assert.True(visionCard.IsActive);

        // kimi-k2 and gpt-5 have no badge and are inactive.
        var kimi = vm.ModelCostRows.Single(r => r.ModelKey == "kimi-k2");
        Assert.Empty(kimi.TierBadges);
        Assert.False(kimi.IsActive);

        var gpt5 = vm.ModelCostRows.Single(r => r.ModelKey == "gpt-5");
        Assert.Empty(gpt5.TierBadges);
        Assert.False(gpt5.IsActive);
    }

    // ── Ordering ────────────────────────────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_BadgedCardsPrecedeUnbadged()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var foundUnbadged = false;
        foreach (var row in vm.ModelCostRows)
        {
            if (row.TierBadges.Count == 0)
                foundUnbadged = true;
            else
                Assert.False(foundUnbadged, $"badged card '{row.ModelKey}' after unbadged");
        }
    }

    [Fact]
    public void PopulateModelCostRows_FirstCardIsCheapModel()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        Assert.Equal("deepseek-v4-flash", vm.ModelCostRows[0].ModelKey);
        Assert.Contains("cheap", vm.ModelCostRows[0].TierBadges);
    }

    // ── Explicit assignments ────────────────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_ExplicitAssignmentWins()
    {
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cheap"] = "kimi-k2",
        };

        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows(assignments);

        var kimi = vm.ModelCostRows.Single(r => r.ModelKey == "kimi-k2");
        Assert.Contains("cheap", kimi.TierBadges);
        Assert.True(kimi.IsActive);

        var flash = vm.ModelCostRows.Single(r => r.ModelKey == "deepseek-v4-flash");
        Assert.Empty(flash.TierBadges);
        Assert.False(flash.IsActive);
    }

    // ── Unpriced assignment ─────────────────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_UnpricedAssignmentYieldsCard()
    {
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vision"] = "hf-qwen3-vl-30b",
        };

        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows(assignments);

        var card = vm.ModelCostRows.Single(r => r.ModelKey == "hf-qwen3-vl-30b");
        Assert.Contains("vision", card.TierBadges);
        Assert.False(card.IsPriced, "unpriced model must have IsPriced=false");
        Assert.True(card.IsActive);
        Assert.Empty(card.InputDisplay);
    }

    // ── Idempotency ─────────────────────────────────────────────────────

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
    public void PopulateModelCostRows_Parameterless_UsesDefaultResolution()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();
        var cards1 = vm.ModelCostRows.Select(r => r.ModelKey)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        var vm2 = new MainWindowViewModel();
        // Explicit null must take the same path as the parameterless call.
        IReadOnlyDictionary<string, string>? explicitNull = null;
        vm2.PopulateModelCostRows(explicitNull);
        var cards2 = vm2.ModelCostRows.Select(r => r.ModelKey)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(cards1, cards2);
    }

    [Fact]
    public void PopulateModelCostRows_AllDefaultCards_ArePriced()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        Assert.All(vm.ModelCostRows, r =>
            Assert.True(r.IsPriced, $"'{r.ModelKey}' should be priced"));
    }

    [Fact]
    public void PopulateModelCostRows_VisionBadgeOnHfQwen3Vl235b()
    {
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var visionCard = vm.ModelCostRows.Single(r => r.ModelKey == "hf-qwen3-vl-235b");
        Assert.Contains("vision", visionCard.TierBadges);
        Assert.True(visionCard.IsActive);
    }
}
