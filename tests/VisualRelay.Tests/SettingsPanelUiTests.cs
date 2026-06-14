using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
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
public sealed class SettingsPanelUiTests : IDisposable
{
    private readonly DictionaryEnvironmentAccessor _env = new();

    public SettingsPanelUiTests()
    {
        KeyEnvFile.EnvironmentAccessorOverride = _env;
    }

    public void Dispose()
    {
        KeyEnvFile.EnvironmentAccessorOverride = null;
        _env.Clear();
    }

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

    [AvaloniaFact]
    public async Task CogOpensSettingsPanel()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        WriteCommitConfig(repo, commitProofArtifacts: true);
        repo.WriteTask("alpha", "# Alpha\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
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

        var vm = new MainWindowViewModel { RootPath = repo.Root };
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
}
