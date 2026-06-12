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

namespace VisualRelay.Tests;

[Collection("Environment")]
public sealed class KeySetupPanelUiTests : IDisposable
{
    private readonly DictionaryEnvironmentAccessor _env = new();

    public KeySetupPanelUiTests()
    {
        KeyEnvFile.EnvironmentAccessorOverride = _env;
    }

    public void Dispose()
    {
        KeyEnvFile.EnvironmentAccessorOverride = null;
        _env.Clear();
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

    private void EnsureNoUserEnv() =>
        _env["XDG_CONFIG_HOME"] = null;

    private sealed class EnvVarRestore(string name, DictionaryEnvironmentAccessor env) : IDisposable
    {
        public void Dispose() => env[name] = null;
    }

    // WaitUntilWithDispatcherAsync is provided by WaitHelpers.

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

    [AvaloniaFact]
    public async Task PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        using var r = SeedUserEnv(repo, "HF_TOKEN=hf-abc123xyz\nDEEPSEEK_API_KEY=sk-deepseek-456\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Click(FindButton(GetTopBar(window), "KeySetupButton"), window);
        Assert.True(vm.IsKeySetupOpen);
        Assert.NotNull(window.GetVisualDescendants().OfType<KeySetupPanel>().FirstOrDefault());

        Assert.Equal(5, MainWindowViewModel.AllProviderKeys.Count);
        Assert.Equal(5, vm.KeyStates.Count);

        var hf = vm.KeyStates.First(s => s.Row.EnvVarName == "HF_TOKEN");
        Assert.True(hf.IsSet);
        Assert.Contains("hf-a", hf.DisplayValue, StringComparison.Ordinal);
        Assert.DoesNotContain("(not set)", hf.DisplayValue, StringComparison.Ordinal);

        Assert.True(vm.KeyStates.First(s => s.Row.EnvVarName == "DEEPSEEK_API_KEY").IsSet);
        foreach (var k in new[] { "MOONSHOT_API_KEY", "ANTHROPIC_API_KEY", "OPENAI_API_KEY" })
        {
            Assert.False(vm.KeyStates.First(s => s.Row.EnvVarName == k).IsSet);
            Assert.Equal("(not set)", vm.KeyStates.First(s => s.Row.EnvVarName == k).DisplayValue);
        }

        Assert.True(vm.IsHuggingFaceConfigured);
        Assert.Equal(string.Empty, vm.HfGateMessage);
        foreach (var row in MainWindowViewModel.AllProviderKeys)
            Assert.StartsWith("https://", row.GetKeyUrl!, StringComparison.Ordinal);
        Assert.Contains("pay-as-you-go", vm.HfPricingNote, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task WithoutHfToken_RunIsBlockedWithMessage_BrowsingStillWorks()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        using var r = SeedUserEnv(repo, "DEEPSEEK_API_KEY=sk-deepseek-456\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();

        Assert.Equal(2, vm.Tasks.Count);
        Assert.False(vm.IsHuggingFaceConfigured);
        Assert.Contains("Hugging Face", vm.HfGateMessage, StringComparison.OrdinalIgnoreCase);

        vm.SelectedTask = vm.Tasks[0];
        await WaitHelpers.WaitUntilWithDispatcherAsync(() => vm.SelectedTaskMarkdown.Contains("Alpha"));
        Assert.True(vm.RunSelectedCommand.CanExecute(null));
        Assert.True(vm.DrainQueueCommand.CanExecute(null));

        await vm.RunSelectedCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsBusy);
        Assert.Contains("Hugging Face", vm.StatusText, StringComparison.OrdinalIgnoreCase);

        vm.ShowArchive = true; Assert.True(vm.ShowArchive); vm.ShowArchive = false;
        vm.SelectedTask = vm.Tasks[1]; Assert.Equal("beta", vm.SelectedTask.Id);
        await WaitHelpers.WaitUntilWithDispatcherAsync(() => vm.SelectedTaskMarkdown.Contains("Beta"));

        vm.SelectedTask = vm.Tasks[0];
        await vm.DrainQueueCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsBusy);
        Assert.Contains("Hugging Face", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task PastingHfTokenAndSaving_WritesEnv_FlipsIsHuggingFaceConfigured_EnablesRun()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        using var r = SeedUserEnv(repo, "");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsHuggingFaceConfigured);
        Click(FindButton(GetTopBar(window), "KeySetupButton"), window);
        Assert.True(vm.IsKeySetupOpen);

        var panel = window.GetVisualDescendants().OfType<KeySetupPanel>().First();
        var input = panel.FindControl<TextBox>("HfTokenInput")!;
        input.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("hf-pasted-token-789");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("hf-pasted-token-789",
            vm.KeyStates.First(s => s.Row.EnvVarName == "HF_TOKEN").PendingValue);

        Click(panel.FindControl<Button>("HfSaveButton")!, window);
        await WaitHelpers.WaitUntilWithDispatcherAsync(() => vm.IsHuggingFaceConfigured);

        Assert.True(vm.IsHuggingFaceConfigured);
        Assert.Equal(string.Empty, vm.HfGateMessage);

        var envPath = Path.Combine(repo.Root, "visual-relay", ".env");
        Assert.Contains("HF_TOKEN=hf-pasted-token-789",
            await File.ReadAllTextAsync(envPath), StringComparison.Ordinal);
        Assert.Equal("hf-pasted-token-789", KeyEnvFile.Read(envPath)["HF_TOKEN"]);
        Assert.True(vm.RunSelectedCommand.CanExecute(null));

        var hf = vm.KeyStates.First(s => s.Row.EnvVarName == "HF_TOKEN");
        Assert.True(hf.IsSet);
        Assert.Contains("hf-p", hf.DisplayValue, StringComparison.Ordinal);
        Assert.Equal(string.Empty, hf.PendingValue);
    }

    [Fact]
    public async Task LitTierIndicators_MatchPresentKeySet_HfOnly_Vs_HfPlusDeepSeek()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        using var r = SeedUserEnv(repo, "HF_TOKEN=hf-abc\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        Assert.True(vm.IsHuggingFaceConfigured);
        Assert.Contains("fallback", vm.LitTiersSummary!, StringComparison.Ordinal);
        Assert.Contains("HF_TOKEN", vm.LitTiersSummary!, StringComparison.Ordinal);
        Assert.Contains("claude→(absent)", vm.LitTiersSummary!, StringComparison.Ordinal);

        KeyEnvFile.Upsert(Path.Combine(repo.Root, "visual-relay", ".env"),
            "DEEPSEEK_API_KEY", "sk-deepseek-xyz");
        await vm.RefreshKeyStatesAsync();
        Assert.Contains("DEEPSEEK_API_KEY", vm.LitTiersSummary!, StringComparison.Ordinal);
        Assert.Contains("deepseek", vm.LitTiersSummary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IsHuggingFaceConfigured_FlipsWithHfTokenPresence()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        using var r = SeedUserEnv(repo, "");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        Assert.False(vm.IsHuggingFaceConfigured);
        Assert.Equal("Set a free Hugging Face token to run tasks — open Keys.", vm.HfGateMessage);

        KeyEnvFile.Upsert(Path.Combine(repo.Root, "visual-relay", ".env"), "HF_TOKEN", "hf-test-token");
        await vm.RefreshKeyStatesAsync();
        Assert.True(vm.IsHuggingFaceConfigured);
        Assert.Equal(string.Empty, vm.HfGateMessage);

        // Set HF_TOKEN in the fake accessor — process env always wins, so
        // RefreshKeyStatesAsync should still see it as configured.
        _env["HF_TOKEN"] = "hf-from-env";
        await vm.RefreshKeyStatesAsync();
        Assert.True(vm.IsHuggingFaceConfigured);
        // Clean up the fake accessor entry.
        _env["HF_TOKEN"] = null;
    }

    [AvaloniaFact]
    public async Task SaveKeyCommand_UpsertsUserEnv_PreservingOtherKeys()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        using var r = SeedUserEnv(repo, "# comment\nDEEPSEEK_API_KEY=sk-existing\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        var ms = vm.KeyStates.First(s => s.Row.EnvVarName == "MOONSHOT_API_KEY");
        Assert.False(ms.IsSet);
        ms.PendingValue = "sk-moonshot-new";
        await vm.SaveKeyCommand.ExecuteAsync(ms);
        Dispatcher.UIThread.RunJobs();

        var contents = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, "visual-relay", ".env"));
        Assert.Contains("DEEPSEEK_API_KEY=sk-existing", contents, StringComparison.Ordinal);
        Assert.Contains("MOONSHOT_API_KEY=sk-moonshot-new", contents, StringComparison.Ordinal);

        var dict = KeyEnvFile.Read(Path.Combine(repo.Root, "visual-relay", ".env"));
        Assert.Equal("sk-existing", dict["DEEPSEEK_API_KEY"]);
        Assert.Equal("sk-moonshot-new", dict["MOONSHOT_API_KEY"]);
        Assert.True(vm.KeyStates.First(s => s.Row.EnvVarName == "MOONSHOT_API_KEY").IsSet);
    }

    [AvaloniaFact]
    public async Task HfGateMessage_AppearsInStatusText_WhenRunIsBlocked()
    {
        EnsureNoUserEnv();
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        using var r = SeedUserEnv(repo, "");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        vm.SelectedTask = vm.Tasks[0];
        var before = vm.StatusText;
        await vm.RunSelectedCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(before, vm.StatusText);
        Assert.Equal(vm.HfGateMessage, vm.StatusText);
        Assert.Contains("Hugging Face", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }
}
