using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Saved bridge settings load wherever they could be saved. Found tracing the Windows arm: Save
/// writes the .env through the config directory, which on Windows comes from APPDATA because there
/// is no HOME, while Load returned defaults whenever HOME was unset, so settings saved in the app
/// were gone on the next start. A config directory without HOME stands in for Windows here.
/// </summary>
public sealed class ObsidianBridgeSettingsNoHomeTests : IDisposable
{
    private readonly string _configHome = Path.Combine(Path.GetTempPath(), "vr-obsidian-nohome", Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFileSystem.DeleteDirectoryResilient(_configHome);

    [Fact]
    public void Load_WithAConfigDirectoryButNoHome_ReadsTheSavedSettings()
    {
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = _configHome };
        Directory.CreateDirectory(Path.Combine(_configHome, "visual-relay"));
        File.WriteAllText(Path.Combine(_configHome, "visual-relay", ".env"),
            "VR_OBSIDIAN_ENABLED=true\nVR_OBSIDIAN_VAULT_ROOT=/vaults/work\nVR_OBSIDIAN_POLL_SECONDS=90\n");

        var config = ObsidianBridgeSettings.Load(env, useIcloudDefault: false);

        Assert.True(config.Enabled);
        Assert.Equal("/vaults/work", config.VaultRoot);
        Assert.Equal(90, config.PollSeconds);
    }

    [Fact]
    public void Load_WithNoConfigDirectoryAtAll_ReturnsTheDefaults()
    {
        var config = ObsidianBridgeSettings.Load(new DictionaryEnvironmentAccessor(), useIcloudDefault: false);

        Assert.False(config.Enabled);
        Assert.Equal(60, config.PollSeconds);
    }
}
