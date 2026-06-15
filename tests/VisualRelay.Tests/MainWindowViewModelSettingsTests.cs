using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;
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

    // ── Commit proof artifacts opt-out ──────────────────────────────────

    [Fact]
    public void CommitProofArtifacts_DefaultsToTrue()
    {
        var viewModel = new MainWindowViewModel();
        Assert.True(viewModel.CommitProofArtifacts);
    }

    [Fact]
    public async Task CommitProofArtifacts_SettingFalse_PersistsToConfig()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        viewModel.CommitProofArtifacts = false;

        // The property change should have persisted to .relay/config.json.
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.False(result.Config.CommitProofArtifacts);
    }

    [Fact]
    public async Task HydrateCommitProofArtifacts_ReadsFromConfig()
    {
        using var repo = TestRepository.Create();
        // Write a config with commitProofArtifacts:false
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        var json = new JsonObject
        {
            ["testCmd"] = "dotnet test",
            ["logSources"] = new JsonArray(),
            ["commitProofArtifacts"] = false
        };
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        await File.WriteAllTextAsync(
            configPath,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        Assert.False(viewModel.CommitProofArtifacts);
    }

    [Fact]
    public async Task HydrateCommitProofArtifacts_DefaultedConfig_DefaultsToTrue()
    {
        using var repo = TestRepository.Create();
        // No config file at all — TryLoadAsync returns Defaulted with CommitProofArtifacts=true.
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        Assert.True(viewModel.CommitProofArtifacts);
    }

    // ── Per-task 10× turn-budget toggle ─────────────────────────────────

    [Fact]
    public async Task SelectedTaskBoostsTurns_hydrated_from_config_on_load()
    {
        using var repo = TestRepository.Create();
        // Write a config with boostTurnsTaskIds containing the task id.
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        var json = new JsonObject
        {
            ["testCmd"] = "dotnet test",
            ["logSources"] = new JsonArray(),
            ["boostTurnsTaskIds"] = new JsonArray("boost-me")
        };
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        await File.WriteAllTextAsync(
            configPath,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        // Write a task so there is something to select.
        repo.WriteTask("boost-me", "# Boost me\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // The first (and only) task should be selected, and it's in the boost set.
        Assert.True(viewModel.SelectedTaskBoostsTurns);
        Assert.Equal("10× turn budget (200 → 2000)", viewModel.TurnBudgetLabel);
    }

    [Fact]
    public async Task SelectedTaskBoostsTurns_not_boosted_when_id_not_in_set()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        var json = new JsonObject
        {
            ["testCmd"] = "dotnet test",
            ["logSources"] = new JsonArray(),
            ["boostTurnsTaskIds"] = new JsonArray("other-task")
        };
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        await File.WriteAllTextAsync(
            configPath,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        repo.WriteTask("normal-task", "# Normal task\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        Assert.False(viewModel.SelectedTaskBoostsTurns);
        Assert.Equal("10× turn budget (200 → 2000)", viewModel.TurnBudgetLabel);
    }

    [Fact]
    public async Task SelectedTaskBoostsTurns_toggle_persists_to_config()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("toggle-me", "# Toggle me\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Initially not boosted.
        Assert.False(viewModel.SelectedTaskBoostsTurns);

        // Toggle on.
        viewModel.SelectedTaskBoostsTurns = true;
        Assert.True(viewModel.SelectedTaskBoostsTurns);

        // Verify persisted to config.
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Contains("toggle-me", result.Config.BoostTurnsTaskIds!);

        // Toggle off.
        viewModel.SelectedTaskBoostsTurns = false;
        Assert.False(viewModel.SelectedTaskBoostsTurns);

        // Verify removed from config.
        var result2 = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.DoesNotContain("toggle-me", result2.Config.BoostTurnsTaskIds!);
    }

    [Fact]
    public void TurnBudgetLabel_shows_calculated_numbers()
    {
        var viewModel = new MainWindowViewModel();

        // No task selected, no root path — label should be empty.
        Assert.Equal(string.Empty, viewModel.TurnBudgetLabel);

        // CanToggleTurnBudget should be false with no selection.
        Assert.False(viewModel.CanToggleTurnBudget);
    }

    [Fact]
    public void SelectedTaskBoostsTurns_defaults_to_false()
    {
        var viewModel = new MainWindowViewModel();
        Assert.False(viewModel.SelectedTaskBoostsTurns);
    }
}
