using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class MainWindowViewModelSettingsTests
{
    [Fact]
    public void BypassSandbox_DefaultsToFalse()
    {
        var viewModel = new MainWindowViewModel();
        Assert.False(viewModel.BypassSandbox);
    }

    [Fact]
    public async Task BypassSandbox_SettingTrue_PersistsToConfig()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        viewModel.BypassSandbox = true;

        // The property change should have persisted to .relay/config.json.
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.True(result.Config.BypassSandbox);
    }

    [Fact]
    public async Task HydrateBypassSandbox_ReadsFromConfig()
    {
        using var repo = TestRepository.Create();
        // Write a config with bypassSandbox:true (WriteConfig doesn't support
        // bypassSandbox, so write the JSON directly).
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        var json = new JsonObject
        {
            ["testCmd"] = "dotnet test",
            ["logSources"] = new JsonArray(),
            ["bypassSandbox"] = true
        };
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        await File.WriteAllTextAsync(
            configPath,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        Assert.True(viewModel.BypassSandbox);
    }

    [Fact]
    public async Task HydrateBypassSandbox_DefaultedConfig_DefaultsToFalse()
    {
        using var repo = TestRepository.Create();
        // No config file at all — TryLoadAsync returns Defaulted with BypassSandbox=false.
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        Assert.False(viewModel.BypassSandbox);
    }
}
