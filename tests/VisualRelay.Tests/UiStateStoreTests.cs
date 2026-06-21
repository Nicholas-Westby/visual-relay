using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class UiStateStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var repo = TestRepository.Create();
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = repo.Root };

        var state = UiStateStore.Load(env);

        Assert.Equal(340, state.ActivityColumnWidth);
        Assert.Equal(0, state.ActivityTabIndex);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var repo = TestRepository.Create();
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = repo.Root };

        var original = new UiState(ActivityColumnWidth: 450, ActivityTabIndex: 3);
        UiStateStore.Save(original, env);

        var loaded = UiStateStore.Load(env);
        Assert.Equal(450, loaded.ActivityColumnWidth);
        Assert.Equal(3, loaded.ActivityTabIndex);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        using var repo = TestRepository.Create();
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = repo.Root };

        // Write garbage JSON to the ui-state.json path.
        var dir = Path.Combine(repo.Root, "visual-relay");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ui-state.json"), "not valid json {{{");

        var state = UiStateStore.Load(env);

        Assert.Equal(340, state.ActivityColumnWidth);
        Assert.Equal(0, state.ActivityTabIndex);
    }

    [Fact]
    public void Save_CreatesDirectory()
    {
        using var repo = TestRepository.Create();
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = repo.Root };

        var visualRelayDir = Path.Combine(repo.Root, "visual-relay");
        Assert.False(Directory.Exists(visualRelayDir));

        UiStateStore.Save(new UiState(ActivityColumnWidth: 500, ActivityTabIndex: 1), env);

        Assert.True(Directory.Exists(visualRelayDir));
        Assert.True(File.Exists(Path.Combine(visualRelayDir, "ui-state.json")));
    }

    [Fact]
    public void Load_NoEnvironmentVariables_ReturnsDefaults()
    {
        // Neither XDG_CONFIG_HOME nor HOME resolves — must not throw, returns
        // defaults. Both are set to empty (treated as unset by the resolver) so
        // the accessor stays authoritative and never leaks the agent's real
        // HOME — an empty accessor would fall through to the process env.
        var env = new DictionaryEnvironmentAccessor
        {
            ["XDG_CONFIG_HOME"] = "",
            ["HOME"] = "",
        };

        var state = UiStateStore.Load(env);

        Assert.Equal(340, state.ActivityColumnWidth);
        Assert.Equal(0, state.ActivityTabIndex);
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTempResidue()
    {
        using var repo = TestRepository.Create();
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = repo.Root };

        UiStateStore.Save(new UiState(ActivityColumnWidth: 420, ActivityTabIndex: 2), env);

        var dir = Path.Combine(repo.Root, "visual-relay");
        Assert.True(File.Exists(Path.Combine(dir, "ui-state.json")));
        // The temp file used for the atomic swap must be moved, not left behind.
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Save_OverwritesExistingFile_AtomicSwapReplacesContent()
    {
        using var repo = TestRepository.Create();
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = repo.Root };

        UiStateStore.Save(new UiState(ActivityColumnWidth: 300, ActivityTabIndex: 0), env);
        // Second save must replace the first via File.Move(overwrite: true).
        UiStateStore.Save(new UiState(ActivityColumnWidth: 600, ActivityTabIndex: 4), env);

        var loaded = UiStateStore.Load(env);
        Assert.Equal(600, loaded.ActivityColumnWidth);
        Assert.Equal(4, loaded.ActivityTabIndex);

        var dir = Path.Combine(repo.Root, "visual-relay");
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }
}
