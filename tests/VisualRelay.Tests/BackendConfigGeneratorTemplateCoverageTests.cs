using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class BackendConfigGeneratorTemplateCoverageTests
{
    [Fact]
    public void Chains_EveryModelExistsInTemplateModelList()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);
        var templateModels = BackendConfigGeneratorTestHelpers.ParseModelNames(yaml);

        foreach (var (tier, candidates) in BackendConfigGenerator.Chains)
            foreach (var (model, _) in candidates)
            {
                if (model == "fallback")
                    continue; // tier alias, not a model_name

                Assert.True(templateModels.Contains(model),
                    $"Chains tier '{tier}' references model '{model}' which is missing from the template model_list");
            }
    }

    [Fact]
    public void SelectableModelsByTier_EveryModelExistsInTemplateModelList()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);
        var templateModels = BackendConfigGeneratorTestHelpers.ParseModelNames(yaml);

        foreach (var (tier, models) in BackendConfigGenerator.SelectableModelsByTier)
            foreach (var model in models)
                Assert.True(templateModels.Contains(model),
                    $"SelectableModelsByTier tier '{tier}' references model '{model}' which is missing from the template model_list");
    }
}
