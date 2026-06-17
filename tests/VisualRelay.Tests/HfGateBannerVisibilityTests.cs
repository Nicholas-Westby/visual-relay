using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// VM visibility tests for the HF-gate banner: <see cref="MainWindowViewModel.ShowHfGate"/>.
/// Asserts the no-flash guard (visibility is false until the first key-state load completes),
/// that visibility flips true when HF_TOKEN is absent, and flips false once a token is set
/// and <see cref="MainWindowViewModel.RefreshKeyStatesAsync"/> runs.
/// </summary>
public sealed class HfGateBannerVisibilityTests
{
    private readonly DictionaryEnvironmentAccessor _env = new();

    private IDisposable SeedUserEnv(TestRepository repo, string content)
    {
        var dir = Path.Combine(repo.Root, "visual-relay");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".env"), content);
        _env["XDG_CONFIG_HOME"] = repo.Root;
        return new EnvVarRestore(_env);
    }

    private sealed class EnvVarRestore(DictionaryEnvironmentAccessor env) : IDisposable
    {
        public void Dispose() => env["XDG_CONFIG_HOME"] = null;
    }

    /// <summary>
    /// No-flash guard: before <see cref="MainWindowViewModel.RefreshKeyStatesAsync"/>
    /// has run, <see cref="MainWindowViewModel.ShowHfGate"/> must be false even though
    /// <see cref="MainWindowViewModel.IsHuggingFaceConfigured"/> starts false.
    /// </summary>
    [Fact]
    public void ShowHfGate_IsFalse_BeforeKeyStatesLoad()
    {
        var vm = new MainWindowViewModel { EnvironmentAccessor = _env };

        // Key states have NOT been loaded yet — gate must be hidden to avoid startup flash.
        Assert.False(vm.ShowHfGate);
    }

    /// <summary>
    /// With no HF token configured, after keys are loaded the gate is visible
    /// and <see cref="MainWindowViewModel.HfGateMessage"/> is the full remediation string.
    /// </summary>
    [Fact]
    public async Task ShowHfGate_IsTrueAndMessageIsFull_WhenNoHfToken()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        using var _ = SeedUserEnv(repo, "DEEPSEEK_API_KEY=sk-deepseek-456\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.RefreshKeyStatesAsync();

        Assert.True(vm.ShowHfGate);
        Assert.False(vm.IsHuggingFaceConfigured);
        Assert.Equal(
            "Set a free Hugging Face token to run tasks — open Settings.",
            vm.HfGateMessage);
    }

    /// <summary>
    /// After a token is set and <see cref="MainWindowViewModel.RefreshKeyStatesAsync"/>
    /// runs, <see cref="MainWindowViewModel.ShowHfGate"/> flips to false.
    /// </summary>
    [Fact]
    public async Task ShowHfGate_FlipsFalse_AfterHfTokenConfigured()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        using var _ = SeedUserEnv(repo, "");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };

        // First load: no token → gate visible.
        await vm.RefreshKeyStatesAsync();
        Assert.True(vm.ShowHfGate);

        // Write the token and refresh.
        KeyEnvFile.Upsert(
            Path.Combine(repo.Root, "visual-relay", ".env"),
            "HF_TOKEN",
            "hf-test-token");
        await vm.RefreshKeyStatesAsync();

        Assert.False(vm.ShowHfGate);
        Assert.True(vm.IsHuggingFaceConfigured);
        Assert.Equal(string.Empty, vm.HfGateMessage);
    }

    /// <summary>
    /// After a full <see cref="MainWindowViewModel.LoadInitialAsync"/>, the gate reflects
    /// the real env state: visible when no token, hidden when a token is present.
    /// </summary>
    [Fact]
    public async Task ShowHfGate_ReflectsRealState_AfterLoadInitialAsync()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        using var _ = SeedUserEnv(repo, "HF_TOKEN=hf-abc123\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.LoadInitialAsync();

        // Token is present → banner must be hidden.
        Assert.False(vm.ShowHfGate);
        Assert.True(vm.IsHuggingFaceConfigured);
    }
}
