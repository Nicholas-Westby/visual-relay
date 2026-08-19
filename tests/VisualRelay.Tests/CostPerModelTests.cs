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
        Assert.Equal("glm-5.3", frontierCard.ModelKey);
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
        // Deliberately synthetic: every model a user can actually select is priced
        // (see AllSelectableModelsArePriced below), so a real name here would only
        // test the unpriced path until that model gained rates.
        const string unpriced = "not-a-real-model-xyz";
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vision"] = unpriced,
        };

        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows(assignments);

        var card = vm.ModelCostRows.Single(r => r.ModelKey == unpriced);
        Assert.Contains("vision", card.TierBadges);
        Assert.False(card.IsPriced, "unpriced model must have IsPriced=false");
        Assert.True(card.IsActive);
        Assert.Empty(card.InputDisplay);
    }

    // ── Pricing coverage ────────────────────────────────────────────────

    [Fact]
    public void PopulateModelCostRows_AllSelectableModelsArePriced()
    {
        // Every model offerable in the tier dropdowns must carry a rate, or the
        // cost panel and run estimates silently under-report when one is picked.
        var vm = new MainWindowViewModel();
        vm.PopulateModelCostRows();

        var priced = vm.ModelCostRows
            .Where(r => r.IsPriced)
            .Select(r => r.ModelKey)
            .ToHashSet(StringComparer.Ordinal);

        var selectable = BackendConfigGenerator.SelectableModelsByTier
            .SelectMany(kv => kv.Value)
            .Distinct(StringComparer.Ordinal);

        foreach (var model in selectable)
            Assert.Contains(model, priced);
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
