using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class MainWindowViewModelSettingsTests
{
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
        // ReSharper disable once UseObjectOrCollectionInitializer — CommitProofArtifacts
        // is set separately on purpose: the test exercises the SETTER's persist side
        // effect, which folding into the initializer would bypass.
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
        await File.WriteAllTextAsync(configPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

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
        await File.WriteAllTextAsync(configPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        // Write a task so there is something to select.
        repo.WriteTask("boost-me", "# Boost me\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // The first (and only) task should be selected, and it's in the boost set.
        Assert.True(viewModel.SelectedTaskBoostsTurns);
        Assert.Equal("10× turn budget (200 → 2000)", viewModel.TurnBudgetLabel);
        Assert.True(viewModel.AreTaskTogglesVisible);
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
        await File.WriteAllTextAsync(configPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
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
        Assert.Equal("10× turn budget (200 → 2000)", viewModel.TurnBudgetLabel);
        Assert.False(viewModel.AreTaskTogglesVisible);
        Assert.False(viewModel.CanToggleTurnBudget);
    }

    [Fact]
    public void SkipTestsLabel_always_shows_text()
    {
        var viewModel = new MainWindowViewModel();
        Assert.Equal("Skip automated testing", viewModel.SkipTestsLabel);
        Assert.False(viewModel.AreTaskTogglesVisible);
    }

    [Fact]
    public void SelectedTaskBoostsTurns_defaults_to_false()
    {
        var viewModel = new MainWindowViewModel();
        Assert.False(viewModel.SelectedTaskBoostsTurns);
    }

    // ── Per-task skip-tests toggle ───────────────────────────────────────

    [Fact]
    public async Task SelectedTaskSkipsTests_hydrated_from_config_on_load()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        var json = new JsonObject
        {
            ["testCmd"] = "dotnet test",
            ["logSources"] = new JsonArray(),
            ["skipTestsTaskIds"] = new JsonArray("readme-only")
        };
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        await File.WriteAllTextAsync(configPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        repo.WriteTask("readme-only", "# README\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        Assert.True(viewModel.SelectedTaskSkipsTests);
        Assert.Equal("Skip automated testing", viewModel.SkipTestsLabel);
    }

    [Fact]
    public async Task SelectedTaskSkipsTests_not_skipped_when_id_not_in_set()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        var json = new JsonObject
        {
            ["testCmd"] = "dotnet test",
            ["logSources"] = new JsonArray(),
            ["skipTestsTaskIds"] = new JsonArray("other-task")
        };
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        await File.WriteAllTextAsync(configPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        repo.WriteTask("normal-task", "# Normal task\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        Assert.False(viewModel.SelectedTaskSkipsTests);
    }

    [Fact]
    public async Task SelectedTaskSkipsTests_toggle_persists_to_config()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("readme-only", "# README\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        Assert.False(viewModel.SelectedTaskSkipsTests);

        viewModel.SelectedTaskSkipsTests = true;
        Assert.True(viewModel.SelectedTaskSkipsTests);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Contains("readme-only", result.Config.SkipTestsTaskIds!);

        viewModel.SelectedTaskSkipsTests = false;
        Assert.False(viewModel.SelectedTaskSkipsTests);

        var result2 = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.DoesNotContain("readme-only", result2.Config.SkipTestsTaskIds!);
    }

    [Fact]
    public void SelectedTaskSkipsTests_defaults_to_false()
    {
        var viewModel = new MainWindowViewModel();
        Assert.False(viewModel.SelectedTaskSkipsTests);
    }

    // ── Timeout minutes ───────────────────────────────────────────────

    [Fact]
    public async Task TimeoutMinutes_hydrate_clamp_and_persist()
    {
        using var repo = TestRepository.Create();
        var viewModel = new MainWindowViewModel();
        Assert.Equal(30, viewModel.StageTimeoutMinutes); Assert.Equal(20, viewModel.TestTimeoutMinutes);

        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        var json = new JsonObject { ["testCmd"] = "dotnet test", ["logSources"] = new JsonArray(), ["subagentTimeoutMs"] = 3_600_001, ["testTimeoutMs"] = 1_200_001 };
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"),
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        viewModel.RootPath = repo.Root;
        await viewModel.LoadInitialAsync();
        Assert.Equal(60, viewModel.StageTimeoutMinutes);
        Assert.Equal(20, viewModel.TestTimeoutMinutes);

        // Verify no write-back on load: config ms values preserved as-is.
        var preChange = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(3_600_001, preChange.Config.SubagentTimeoutMilliseconds); Assert.Equal(1_200_001, preChange.Config.TestTimeoutMilliseconds);

        // Clamp: below → 1, above → 720.
        viewModel.StageTimeoutMinutes = 0; Assert.Equal(1, viewModel.StageTimeoutMinutes);
        viewModel.TestTimeoutMinutes = -5; Assert.Equal(1, viewModel.TestTimeoutMinutes);
        viewModel.StageTimeoutMinutes = 999; Assert.Equal(720, viewModel.StageTimeoutMinutes);

        // Persist and round-trip: minutes → ms.
        viewModel.StageTimeoutMinutes = 45; viewModel.TestTimeoutMinutes = 15;
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(2_700_000, result.Config.SubagentTimeoutMilliseconds);
        Assert.Equal(900_000, result.Config.TestTimeoutMilliseconds);

        // Hand-edited 0 (disable) escape hatch: UI clamps for display, config preserves 0.
        json["subagentTimeoutMs"] = 0; json["testTimeoutMs"] = 0;
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"),
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        await viewModel.LoadInitialAsync();
        Assert.Equal(1, viewModel.StageTimeoutMinutes); Assert.Equal(1, viewModel.TestTimeoutMinutes);
        var final = await RelayConfigLoader.TryLoadAsync(repo.Root); Assert.Equal(0, final.Config.SubagentTimeoutMilliseconds); Assert.Equal(0, final.Config.TestTimeoutMilliseconds);
    }
}
