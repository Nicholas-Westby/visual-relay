using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;
using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class SettingsPanelUiTests
{
    // ── Live Tiers model selection ────────────────────────────────────────

    [AvaloniaFact]
    public async Task LiveTiers_HasComboBoxPerEditableTier()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        repo.WriteTask("alpha", "# Alpha\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.LoadInitialAsync();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var dialog = OpenSettings(window);
        Assert.NotNull(dialog);

        var panel = dialog.GetVisualDescendants().OfType<SettingsPanel>().First();
        var litTierItems = panel.FindControl<ItemsControl>("LitTierItems");
        Assert.NotNull(litTierItems);

        Dispatcher.UIThread.RunJobs();

        var rows = litTierItems.ItemsSource?.Cast<MainWindowViewModel.TierModelRow>().ToList();
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);

        foreach (var row in rows!)
        {
            if (row.IsEditable)
                Assert.True(row.SelectableModels.Count > 0,
                    $"Editable tier '{row.Tier}' should have selectable models.");
            else
                Assert.Equal("fallback", row.Tier);
        }

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task FallbackTier_ComboBoxIsDisabled()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        repo.WriteTask("alpha", "# Alpha\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.LoadInitialAsync();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var dialog = OpenSettings(window);
        Assert.NotNull(dialog);

        Dispatcher.UIThread.RunJobs();

        var panel = dialog.GetVisualDescendants().OfType<SettingsPanel>().First();
        var litTierItems = panel.FindControl<ItemsControl>("LitTierItems");
        Assert.NotNull(litTierItems);

        var rows = litTierItems.ItemsSource?.Cast<MainWindowViewModel.TierModelRow>().ToList();
        Assert.NotNull(rows);

        var fallback = rows!.FirstOrDefault(r => r.Tier == "fallback");
        Assert.NotNull(fallback);
        Assert.False(fallback!.IsEditable);

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task SelectingModel_WritesTierModelOverrides()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        repo.WriteTask("alpha", "# Alpha\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.LoadInitialAsync();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var dialog = OpenSettings(window);
        Assert.NotNull(dialog);

        Dispatcher.UIThread.RunJobs();

        var panel = dialog.GetVisualDescendants().OfType<SettingsPanel>().First();
        var litTierItems = panel.FindControl<ItemsControl>("LitTierItems");
        Assert.NotNull(litTierItems);

        var rows = litTierItems.ItemsSource?.Cast<MainWindowViewModel.TierModelRow>().ToList();
        Assert.NotNull(rows);

        var cheap = rows!.FirstOrDefault(r => r.Tier == "cheap");
        Assert.NotNull(cheap);
        Assert.True(cheap!.IsEditable);
        Assert.True(cheap.SelectableModels.Count >= 2);

        var original = cheap.SelectedModel;
        var alternative = cheap.SelectableModels.FirstOrDefault(m => m != original);
        Assert.NotNull(alternative);

        cheap.SelectedModel = alternative!;
        Dispatcher.UIThread.RunJobs();

        var configResult = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, configResult.Status);
        Assert.NotNull(configResult.Config.TierModelOverrides);
        Assert.Equal(alternative, configResult.Config.TierModelOverrides!["cheap"]);

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
