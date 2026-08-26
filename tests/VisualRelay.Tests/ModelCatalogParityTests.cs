using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

public sealed class ModelCatalogParityTests
{
    [Fact]
    public void TemplateModelList_MatchesRelayPricingDefaultKeys()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);

        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            yaml,
            pricingKeys: RelayPricing.Default.Keys);

        Assert.Empty(problems);
    }

    [Fact]
    public void PricingParityGuard_NegativeControl_ReportsBothSides()
    {
        const string template =
            "model_list:\n" +
            "  - model_name: a-model\n" +
            "  - model_name: shared-model\n" +
            "  - model_name: template-only-model\n" +
            "router_settings:\n" +
            "  routing_strategy: usage-based-routing-v2\n";

        string[] pricingKeys =
        [
            "a-model",
            "shared-model",
            "pricing-only-model",
        ];

        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            template,
            pricingKeys: pricingKeys);

        Assert.Contains("routable but not priced: template-only-model", problems);
        Assert.Contains("priced but not routable: pricing-only-model", problems);
    }

    [Fact]
    public void ChainsParityGuard_NegativeControl_ReportsMissingModel()
    {
        const string template =
            "model_list:\n" +
            "  - model_name: a-model\n" +
            "router_settings:\n" +
            "  routing_strategy: usage-based-routing-v2\n";

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
            template,
            chains: chains);

        Assert.Contains("chained but not routable: ghost-model (tier cheap)", problems);
        Assert.DoesNotContain(problems, p => p.Contains("fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectableParityGuard_NegativeControl_ReportsMissingModel()
    {
        const string template =
            "model_list:\n" +
            "  - model_name: a-model\n" +
            "router_settings:\n" +
            "  routing_strategy: usage-based-routing-v2\n";

        var selectable = new Dictionary<string, IReadOnlyList<string>>
        {
            ["frontier"] = new List<string> { "a-model", "ghost-model" },
        };

        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            template,
            selectable: selectable);

        Assert.Contains("selectable but not routable: ghost-model (tier frontier)", problems);
    }
}
