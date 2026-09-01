using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

public sealed class ModelCatalogParityTests
{
    /// <summary>
    /// Every routable model is priced and every priced model is routable.
    /// <para>
    /// This compared the proxy's YAML <c>model_list</c> against pricing. The
    /// routes are the list now. Adding a route without a price, or leaving a
    /// price behind after removing a route, fails here.
    /// </para>
    /// </summary>
    [Fact]
    public void RoutableModels_MatchRelayPricingDefaultKeys()
    {
        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            BackendConfigGeneratorTestHelpers.RoutableModels(),
            pricingKeys: RelayPricing.Default.Keys);

        Assert.Empty(problems);
    }

    /// <summary>
    /// Every routable model also has a goldened request body. Adding a route
    /// without goldening what it sends fails the build, which is the
    /// self-maintaining half: the catalog constant drives the required set.
    /// </summary>
    [Fact]
    public void EveryRoutableModel_HasAGoldenedRequestBody()
    {
        var goldened = RequestGolden.All()
            .Select(g => g.Model)
            .ToHashSet(StringComparer.Ordinal);

        var missing = BackendConfigGeneratorTestHelpers.RoutableModels()
            .Where(model => !goldened.Contains(model))
            .OrderBy(model => model, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "routable models with no goldened request body: " + string.Join(", ", missing));
    }

    [Fact]
    public void PricingParityGuard_NegativeControl_ReportsBothSides()
    {
        var routable = new HashSet<string>(StringComparer.Ordinal)
        {
            "a-model", "shared-model", "routable-only-model",
        };

        string[] pricingKeys =
        [
            "a-model",
            "shared-model",
            "pricing-only-model",
        ];

        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            routable,
            pricingKeys: pricingKeys);

        Assert.Contains("routable but not priced: routable-only-model", problems);
        Assert.Contains("priced but not routable: pricing-only-model", problems);
    }

    [Fact]
    public void ChainsParityGuard_NegativeControl_ReportsMissingModel()
    {
        var routable = new HashSet<string>(StringComparer.Ordinal) { "a-model" };

        var chains = new Dictionary<string, List<(string Model, string RequiredKey)>>
        {
            ["cheap"] =
            [
                ("a-model", "ANY_KEY"),
                ("ghost-model", "ANY_KEY"),
                ("fallback", "ANY_KEY"),
            ],
        };

        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            routable,
            chains: chains);

        Assert.Contains("chained but not routable: ghost-model (tier cheap)", problems);
        Assert.DoesNotContain(problems, p => p.Contains("fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectableParityGuard_NegativeControl_ReportsMissingModel()
    {
        var routable = new HashSet<string>(StringComparer.Ordinal) { "a-model" };

        var selectable = new Dictionary<string, IReadOnlyList<string>>
        {
            ["frontier"] = new List<string> { "a-model", "ghost-model" },
        };

        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            routable,
            selectable: selectable);

        Assert.Contains("selectable but not routable: ghost-model (tier frontier)", problems);
    }
}
