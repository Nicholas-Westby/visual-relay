using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class BackendConfigGeneratorTemplateCoverageTests
{
    [Fact]
    public void Chains_EveryModelExistsInTemplateModelList()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);

        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            yaml,
            chains: BackendConfigGenerator.Chains);

        Assert.Empty(problems);
    }

    [Fact]
    public void SelectableModelsByTier_EveryModelExistsInTemplateModelList()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);

        var problems = BackendConfigGeneratorTestHelpers.FindCatalogProblems(
            yaml,
            selectable: BackendConfigGenerator.SelectableModelsByTier);

        Assert.Empty(problems);
    }
}
