using VisualRelay.App.ViewModels;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class TierModelProviderTests
{
    [Fact]
    public async Task OverrideWithAbsentKey_DisplaysOverrideModel_WithItsOwnProvider()
    {
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() };
        using var repo = TestRepository.Create();
        SettingsTestHelpers.WriteCommitConfig(repo, commitProofArtifacts: true);
        using var _ = SettingsTestHelpers.SeedUserEnv(env, repo, "HF_TOKEN=hf-abc\n");
        RelayConfigWriter.UpsertTierModelOverrides(
            repo.Root, new Dictionary<string, string> { ["frontier"] = "gpt-5" });

        var vm = new MainWindowViewModel(env) { RootPath = repo.Root };
        await vm.OpenSettingsAsync();

        var frontier = vm.LitTierRows.First(r => r.Tier == "frontier");
        Assert.Equal("gpt-5", frontier.SelectedModel);
        Assert.Equal("OpenAI", frontier.ProviderName);
    }

    [Fact]
    public void TierModelRow_SelectedModelChange_MovesProviderWithoutRefresh()
    {
        var persistCalls = 0;
        var row = new MainWindowViewModel.TierModelRow
        {
            Tier = "frontier",
            SelectedModel = "glm-5.2",
            OnSelectedModelPersist = _ => { persistCalls++; return Task.CompletedTask; },
        };

        Assert.Equal("Hugging Face", row.ProviderName);

        row.SelectedModel = "kimi-k2";

        Assert.Equal("Moonshot", row.ProviderName);
        Assert.Equal(1, persistCalls);
    }
}
