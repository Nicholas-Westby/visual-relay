using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Every model the catalog chains or offers must actually be routable.
/// <para>
/// The route table is the list of what exists, so a chain or a selectable entry
/// naming a model nothing routes is caught here rather than at the first request
/// that needs it.
/// </para>
/// </summary>
public sealed class ModelCatalogRoutableCoverageTests
{
    /// <summary>Every chained model is routable.</summary>
    [Fact]
    public void Chains_EveryModelIsRoutable()
    {
        var problems = ModelCatalogTestHelpers.FindCatalogProblems(
            ModelCatalogTestHelpers.RoutableModels(),
            chains: ModelCatalog.Chains);

        Assert.Empty(problems);
    }

    /// <summary>Every selectable model is routable.</summary>
    [Fact]
    public void SelectableModelsByTier_EveryModelIsRoutable()
    {
        var problems = ModelCatalogTestHelpers.FindCatalogProblems(
            ModelCatalogTestHelpers.RoutableModels(),
            selectable: ModelCatalog.SelectableModelsByTier);

        Assert.Empty(problems);
    }

    /// <summary>
    /// The negative control: a model absent from the routable set IS reported.
    /// Without this, a guard comparing against an empty set reports green.
    /// </summary>
    [Fact]
    public void TheGuard_ReportsAModelNothingRoutes()
    {
        var problems = ModelCatalogTestHelpers.FindCatalogProblems(
            routable: [],
            selectable: ModelCatalog.SelectableModelsByTier);

        Assert.NotEmpty(problems);
        Assert.All(problems, p => Assert.Contains("selectable but not routable", p, StringComparison.Ordinal));
    }
}
