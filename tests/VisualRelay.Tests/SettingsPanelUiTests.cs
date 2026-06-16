using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;
using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed class SettingsPanelUiTests
{
    private readonly DictionaryEnvironmentAccessor _env = new();

    private void EnsureNoUserEnv() =>
        _env["XDG_CONFIG_HOME"] = null;

    private static Button FindButton(Control root, string name)
    {
        var btn = root.FindControl<Button>(name);
        Assert.NotNull(btn);
        return btn;
    }

    private static void Click(Control target, TopLevel root)
    {
        var c = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        var pt = target.TranslatePoint(c, root) ?? c;
        root.MouseDown(pt, MouseButton.Left);
        root.MouseUp(pt, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static TopBar GetTopBar(Visual window) =>
        window.GetVisualDescendants().OfType<TopBar>().Single();

    /// <summary>
    /// Writes a valid <c>.relay/config.json</c> with the given
    /// <paramref name="commitProofArtifacts"/> value (or omits the key
    /// when null) so the loader treats it as Loaded.
    /// </summary>
    private static void WriteCommitConfig(TestRepository repo, bool? commitProofArtifacts)
    {
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        var json = new JsonObject
        {
            ["testCmd"] = "dotnet test",
            ["logSources"] = new JsonArray()
        };
        if (commitProofArtifacts is { } value)
        {
            json["commitProofArtifacts"] = value;
        }
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        File.WriteAllText(
            configPath,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    /// <summary>
    /// Seeds the fake environment accessor with XDG_CONFIG_HOME pointing to
    /// <paramref name="repo"/>.Root, writes the given <paramref name="content"/>
    /// to the <c>.env</c> file under <c>visual-relay/</c>, and returns a
    /// disposable that clears XDG_CONFIG_HOME from the accessor.
    /// </summary>
    private IDisposable SeedUserEnv(TestRepository repo, string content)
    {
        var dir = Path.Combine(repo.Root, "visual-relay");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".env"), content);
        _env["XDG_CONFIG_HOME"] = repo.Root;
        return new EnvVarRestore("XDG_CONFIG_HOME", _env);
    }

    private sealed class EnvVarRestore(string name, DictionaryEnvironmentAccessor env) : IDisposable
    {
        public void Dispose() => env[name] = null;
    }

    [AvaloniaFact]
    public async Task CogOpensSettingsPanel()
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

        Click(FindButton(GetTopBar(window), "SettingsButton"), window);
        Assert.True(vm.IsSettingsOpen);
        Assert.NotNull(window.GetVisualDescendants().OfType<SettingsPanel>().FirstOrDefault());
    }

    [AvaloniaFact]
    public async Task ToggleCommitProofArtifacts_WritesConfig()
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

        // Open the Settings panel via the cog button.
        Click(FindButton(GetTopBar(window), "SettingsButton"), window);
        Assert.True(vm.IsSettingsOpen);

        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().First();
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
    }

    // ── Consolidated Settings panel tests ────────────────────────────────────

    [AvaloniaFact]
    public async Task SettingsPanelContainsScrollViewerWithNoHorizontalScroll()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        repo.WriteTask("alpha", "# Alpha\n");
        using var r = SeedUserEnv(repo, "HF_TOKEN=hf-test\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.LoadInitialAsync();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Click(FindButton(GetTopBar(window), "SettingsButton"), window);
        Assert.True(vm.IsSettingsOpen);

        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().First();
        var scrollViewer = panel.FindControl<ScrollViewer>("SettingsScrollViewer");
        Assert.NotNull(scrollViewer);
        Assert.Equal(ScrollBarVisibility.Disabled, scrollViewer.HorizontalScrollBarVisibility);
    }

    [AvaloniaFact]
    public async Task SettingsPanelShowsBothCommitProofCheckboxAndProviderKeyRows()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        repo.WriteTask("alpha", "# Alpha\n");
        using var r = SeedUserEnv(repo, "HF_TOKEN=hf-test\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.LoadInitialAsync();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Click(FindButton(GetTopBar(window), "SettingsButton"), window);
        Assert.True(vm.IsSettingsOpen);

        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().First();

        // Commit proof checkbox must still be present.
        var commitCheckBox = panel.FindControl<CheckBox>("CommitProofCheckBox");
        Assert.NotNull(commitCheckBox);
        Assert.True(commitCheckBox.IsChecked);

        // Provider key rows must be present — the named HF controls are the
        // canonical smoke test that the key rows were copied into SettingsPanel.
        var hfInput = panel.FindControl<TextBox>("HfTokenInput");
        Assert.NotNull(hfInput);

        var hfSave = panel.FindControl<Button>("HfSaveButton");
        Assert.NotNull(hfSave);
    }

    [Fact]
    public async Task ToggleSettingsRefreshesKeyStatesOnOpen()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        using var r = SeedUserEnv(repo, "HF_TOKEN=hf-from-env-file\nDEEPSEEK_API_KEY=sk-deepseek-999\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.LoadInitialAsync();

        // Before toggling settings, key states should already be populated
        // by LoadInitialAsync, but ToggleSettings should refresh them again.
        // Clear them to verify ToggleSettings repopulates.
        vm.KeyStates.Clear();
        Assert.Empty(vm.KeyStates);

        vm.ToggleSettingsCommand.Execute(null);
        Assert.True(vm.IsSettingsOpen);

        // KeyStates must be repopulated after ToggleSettings opens the panel.
        Assert.Equal(5, vm.KeyStates.Count);
        Assert.True(vm.KeyStates.First(s => s.Row.EnvVarName == "HF_TOKEN").IsSet);
        Assert.True(vm.KeyStates.First(s => s.Row.EnvVarName == "DEEPSEEK_API_KEY").IsSet);
    }

    [AvaloniaFact]
    public async Task KeySetupButtonIsAbsentFromTopBar()
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

        var topBar = GetTopBar(window);
        // The separate "Keys" button must be gone after consolidation.
        var keyButton = topBar.FindControl<Button>("KeySetupButton");
        Assert.Null(keyButton);

        // The Settings cog must still be present.
        var settingsButton = topBar.FindControl<Button>("SettingsButton");
        Assert.NotNull(settingsButton);
    }
}
