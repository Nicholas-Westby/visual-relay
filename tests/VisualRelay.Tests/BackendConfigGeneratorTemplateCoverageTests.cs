using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Every model the catalog chains or offers must actually be routable.
/// <para>
/// This compared against the proxy's YAML <c>model_list</c>. The routes are the
/// list now, so a chain or a selectable entry naming a model nothing routes is
/// caught here rather than at the first request that needs it.
/// </para>
/// </summary>
public sealed class BackendConfigGeneratorTemplateCoverageTests
{
    /// <summary>Every chained model is routable.</summary>
    [Fact]
    public void Chains_EveryModelIsRoutable()
    {
        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            BackendConfigGeneratorTestHelpers.RoutableModels(),
            chains: BackendConfigGenerator.Chains);

        Assert.Empty(problems);
    }

    /// <summary>Every selectable model is routable.</summary>
    [Fact]
    public void SelectableModelsByTier_EveryModelIsRoutable()
    {
        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            BackendConfigGeneratorTestHelpers.RoutableModels(),
            selectable: BackendConfigGenerator.SelectableModelsByTier);

        Assert.Empty(problems);
    }

    /// <summary>
    /// The negative control: a model absent from the routable set IS reported.
    /// Without this, a guard comparing against an empty set reports green.
    /// </summary>
    [Fact]
    public void TheGuard_ReportsAModelNothingRoutes()
    {
        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            routable: [],
            selectable: BackendConfigGenerator.SelectableModelsByTier);

        Assert.NotEmpty(problems);
        Assert.All(problems, p => Assert.Contains("selectable but not routable", p, StringComparison.Ordinal));
    }
}
