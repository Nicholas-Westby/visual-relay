using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;
using VisualRelay.App.Views.Controls.Buttons;
using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed partial class SettingsPanelUiTests
{
    private readonly DictionaryEnvironmentAccessor _env = new() { ["XDG_CONFIG_HOME"] = Path.GetTempPath() };

    // Thin forwarders to SettingsTestHelpers keep these tests under the
    // source-size guard and remove duplication with the other UI test classes.
    private void EnsureNoUserEnv() => SettingsTestHelpers.EnsureNoUserEnv(_env);
    private IDisposable SeedUserEnv(TestRepository repo, string content) =>
        SettingsTestHelpers.SeedUserEnv(_env, repo, content);
    private static void WriteCommitConfig(TestRepository repo, bool? commitProofArtifacts) =>
        SettingsTestHelpers.WriteCommitConfig(repo, commitProofArtifacts);

    // Scoped-down construction: build the settings panel under test (a
    // SettingsWindow bound to the VM) without the whole MainWindow, the cog, or
    // LoadInitialAsync. OpenSettingsAsync populates the same panel state the cog
    // path does. See SettingsTestHelpers.ShowScopedSettings for why this also
    // removes the async-void spin-loop that flaked under parallel load.
    private async Task<SettingsWindow> OpenScopedSettingsAsync(TestRepository repo)
    {
        var vm = new MainWindowViewModel(_env) { RootPath = repo.Root };
        await vm.OpenSettingsAsync();
        return SettingsTestHelpers.ShowScopedSettings(vm);
    }

    [AvaloniaFact]
    public async Task CogOpensSettingsPanel()
    {
        // WHOLE-APP wiring: the cog click and the close→IsSettingsOpen reset are
        // owned by the MainWindow/TopBar cog handler, so this fact keeps the full
        // MainWindow boot (allowlisted in NoWholeAppBootGuardTests).
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        repo.WriteTask("alpha", "# Alpha\n");

        var vm = new MainWindowViewModel(_env) { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var dialog = SettingsTestHelpers.OpenSettings(window);
        Assert.True(vm.IsSettingsOpen);
        Assert.NotNull(dialog.GetVisualDescendants().OfType<SettingsPanel>().FirstOrDefault());

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsSettingsOpen);
    }

    [AvaloniaFact]
    public async Task ToggleCommitProofArtifacts_WritesConfig()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);

        var dialog = await OpenScopedSettingsAsync(repo);
        var vm = (MainWindowViewModel)dialog.DataContext!;
        Assert.True(vm.IsSettingsOpen);

        var panel = dialog.GetVisualDescendants().OfType<SettingsPanel>().First();
        var checkBox = panel.FindControl<CheckBox>("CommitProofCheckBox")!;
        Assert.NotNull(checkBox);

        // It should start checked (true).
        Assert.True(checkBox.IsChecked);
        Assert.True(vm.CommitProofArtifacts);

        // Uncheck it.
        checkBox.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.CommitProofArtifacts);

        // The config file should now have commitProofArtifacts: false.
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.False(result.Config.CommitProofArtifacts);

        // Other keys must be preserved.
        Assert.Equal("dotnet test", result.Config.TestCommand);
        Assert.Empty(result.Config.LogSources);

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── Consolidated Settings panel tests ────────────────────────────────────

    [AvaloniaFact]
    public async Task SettingsPanelContainsScrollViewerWithNoHorizontalScroll()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        using var r = SeedUserEnv(repo, "HF_TOKEN=hf-test\n");

        var dialog = await OpenScopedSettingsAsync(repo);
        var vm = (MainWindowViewModel)dialog.DataContext!;
        Assert.True(vm.IsSettingsOpen);

        var panel = dialog.GetVisualDescendants().OfType<SettingsPanel>().First();
        var scrollViewer = panel.FindControl<ScrollViewer>("SettingsScrollViewer");
        Assert.NotNull(scrollViewer);
        Assert.Equal(ScrollBarVisibility.Disabled, scrollViewer.HorizontalScrollBarVisibility);

        // Core fix: exactly ONE layout scroll region in the whole settings dialog
        // — the old flyout added a second (FlyoutPresenter) scrollbar that clipped
        // "Live Tiers". The modal owns the single scroll region.
        Assert.Single(SettingsTestHelpers.LayoutScrollViewers(dialog));

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task SettingsPanelShowsBothCommitProofCheckboxAndProviderKeyRows()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        using var r = SeedUserEnv(repo, "HF_TOKEN=hf-test\n");

        var dialog = await OpenScopedSettingsAsync(repo);
        var vm = (MainWindowViewModel)dialog.DataContext!;
        Assert.True(vm.IsSettingsOpen);

        var panel = dialog.GetVisualDescendants().OfType<SettingsPanel>().First();

        // Commit proof checkbox must still be present.
        var commitCheckBox = panel.FindControl<CheckBox>("CommitProofCheckBox");
        Assert.NotNull(commitCheckBox);
        Assert.True(commitCheckBox.IsChecked);

        // Provider key rows must be present — the named HF controls are the
        // canonical smoke test that the key rows were copied into SettingsPanel.
        var hfInput = panel.FindControl<TextBox>("HfTokenInput");
        Assert.NotNull(hfInput);

        var hfSave = panel.FindControl<CommonButton>("HfSaveButton");
        Assert.NotNull(hfSave);

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task OpenSettingsRefreshesKeyStatesOnOpen()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        using var r = SeedUserEnv(repo, "HF_TOKEN=hf-from-env-file\nDEEPSEEK_API_KEY=sk-deepseek-999\n");

        // VM-only fact: no window at all. KeyStates start empty; OpenSettingsAsync
        // must populate them (the behaviour the cog relies on).
        var vm = new MainWindowViewModel(_env) { RootPath = repo.Root };
        Assert.Empty(vm.KeyStates);

        await vm.OpenSettingsAsync();
        Assert.True(vm.IsSettingsOpen);

        // KeyStates must be repopulated after OpenSettingsAsync opens the dialog.
        // Counted off AllProviderKeys rather than a literal: the point is "one
        // state per provider", not "five providers".
        Assert.Equal(MainWindowViewModel.AllProviderKeys.Count, vm.KeyStates.Count);
        Assert.True(vm.KeyStates.First(s => s.Row.EnvVarName == "HF_TOKEN").IsSet);
        Assert.True(vm.KeyStates.First(s => s.Row.EnvVarName == "DEEPSEEK_API_KEY").IsSet);

        vm.CloseSettings();
        Assert.False(vm.IsSettingsOpen);
    }

    [AvaloniaFact]
    public void KeySetupButtonIsAbsentFromTopBar()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);

        // Scoped to just the TopBar under test (hosted in a bare window so the
        // control renders) — the top-bar composition needs no MainWindow.
        var vm = new MainWindowViewModel(_env) { RootPath = repo.Root };
        var topBar = new TopBar { DataContext = vm };
        var host = new Window { Content = topBar, Width = 1440, Height = 120 };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        // The separate "Keys" button must be gone after consolidation.
        var keyButton = topBar.FindControl<CommonButton>("KeySetupButton");
        Assert.Null(keyButton);

        // The Settings cog must still be present.
        var settingsButton = topBar.FindControl<CommonButton>("SettingsButton");
        Assert.NotNull(settingsButton);

        host.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task SettingsPanelHasRevealSettingsFileButton()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        using var r = SeedUserEnv(repo, "HF_TOKEN=hf-test\n");

        var dialog = await OpenScopedSettingsAsync(repo);
        var vm = (MainWindowViewModel)dialog.DataContext!;
        Assert.True(vm.IsSettingsOpen);

        var panel = dialog.GetVisualDescendants().OfType<SettingsPanel>().First();

        var revealButton = panel.FindControl<CommonButton>("RevealSettingsFileButton");
        Assert.NotNull(revealButton);
        Assert.NotNull(revealButton.Command);

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
