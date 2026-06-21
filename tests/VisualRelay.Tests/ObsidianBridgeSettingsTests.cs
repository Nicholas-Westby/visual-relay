using System.Text.Json;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class ObsidianBridgeSettingsTests : IDisposable
{
    private readonly DictionaryEnvironmentAccessor _env = new();
    private readonly string _tempHome;

    public ObsidianBridgeSettingsTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "vr-obsidian-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
    }

    public void Dispose()
    {
        TestFileSystem.DeleteDirectoryResilient(_tempHome);
    }

    // ── Path resolution ────────────────────────────────────────────────

    [Fact]
    public void ResolvePath_WithXdgConfigHome_UsesXdgConfigHome()
    {
        var xdg = "/custom/xdg/config";
        var path = ObsidianBridgeSettings.ResolvePath(xdg, "/home/user");
        Assert.StartsWith(xdg + "/visual-relay/obsidian.json", path, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePath_WithoutXdgConfigHome_FallsBackToHomeDotConfig()
    {
        var home = "/home/user";
        var path = ObsidianBridgeSettings.ResolvePath(xdgConfigHome: null, home);
        Assert.StartsWith(home + "/.config/visual-relay/obsidian.json", path, StringComparison.Ordinal);
    }

    // ── Load defaults ──────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        _env["HOME"] = _tempHome;
        _env["XDG_CONFIG_HOME"] = null;

        var config = ObsidianBridgeSettings.Load(_env);

        Assert.False(config.Enabled);
        Assert.Equal(_tempHome + "/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/",
            config.VaultRoot);
        Assert.Equal(60, config.PollSeconds);
    }

    [Fact]
    public void Load_EnabledDefaultsToFalse()
    {
        _env["HOME"] = _tempHome;

        var config = ObsidianBridgeSettings.Load(_env);

        // The bridge must be opt-in — never enabled by default.
        Assert.False(config.Enabled);
    }

    [Fact]
    public void Load_WhenHomeIsUnset_ReturnsDisabledAndKeepsDefaults()
    {
        _env["HOME"] = null;
        _env["XDG_CONFIG_HOME"] = null;

        // Must degrade gracefully: disabled, default vault root with "~" unexpanded,
        // default poll seconds. Should not throw.
        var config = ObsidianBridgeSettings.Load(_env);

        Assert.False(config.Enabled);
        Assert.Equal(60, config.PollSeconds);
    }

    [Fact]
    public void Load_PollSecondsClampedToMinimum()
    {
        _env["HOME"] = _tempHome;

        // Write a settings file with a too-low poll interval.
        var dir = Path.Combine(_tempHome, ".config", "visual-relay");
        var filePath = Path.Combine(dir, "obsidian.json");
        Directory.CreateDirectory(dir);
        File.WriteAllText(filePath,
            JsonSerializer.Serialize(new { enabled = true, vaultRoot = "/tmp/vault", pollSeconds = 5 }));

        var config = ObsidianBridgeSettings.Load(_env);

        // Must be clamped to the floor (≥ 15).
        Assert.True(config.PollSeconds >= 15);
    }

    [Fact]
    public void Load_ExpandsTildeInVaultRoot()
    {
        _env["HOME"] = _tempHome;

        var dir = Path.Combine(_tempHome, ".config", "visual-relay");
        var filePath = Path.Combine(dir, "obsidian.json");
        Directory.CreateDirectory(dir);
        File.WriteAllText(filePath,
            JsonSerializer.Serialize(new
            {
                enabled = true,
                vaultRoot = "~/Library/Mobile Documents/iCloud~md~obsidian/Documents/VR/",
                pollSeconds = 60
            }));

        var config = ObsidianBridgeSettings.Load(_env);

        Assert.Equal(
            _tempHome + "/Library/Mobile Documents/iCloud~md~obsidian/Documents/VR/",
            config.VaultRoot);
    }

    // ── Round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void SaveAndLoad_RoundTripsAllFields()
    {
        _env["HOME"] = _tempHome;

        var original = new ObsidianBridgeConfig(
            Enabled: true,
            VaultRoot: "/custom/vault/path/",
            PollSeconds: 45);

        ObsidianBridgeSettings.Save(original, _env);
        var loaded = ObsidianBridgeSettings.Load(_env);

        Assert.Equal(original.Enabled, loaded.Enabled);
        Assert.Equal(original.VaultRoot, loaded.VaultRoot);
        Assert.Equal(original.PollSeconds, loaded.PollSeconds);
    }

    [Fact]
    public void SaveAndLoad_DisabledState_RoundTrips()
    {
        _env["HOME"] = _tempHome;

        var original = new ObsidianBridgeConfig(
            Enabled: false,
            VaultRoot: "/another/vault/",
            PollSeconds: 90);

        ObsidianBridgeSettings.Save(original, _env);
        var loaded = ObsidianBridgeSettings.Load(_env);

        Assert.False(loaded.Enabled);
        Assert.Equal("/another/vault/", loaded.VaultRoot);
        Assert.Equal(90, loaded.PollSeconds);
    }

    // ── Malformed JSON ─────────────────────────────────────────────────

    [Fact]
    public void Load_MalformedJson_ReturnsDefaults()
    {
        _env["HOME"] = _tempHome;
        var dir = Path.Combine(_tempHome, ".config", "visual-relay");
        var filePath = Path.Combine(dir, "obsidian.json");
        Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, "this is not json }}{");

        var config = ObsidianBridgeSettings.Load(_env);

        // Must degrade to safe defaults.
        Assert.False(config.Enabled);
        Assert.Contains(_tempHome, config.VaultRoot, StringComparison.Ordinal);
        Assert.Equal(60, config.PollSeconds);
    }

    // ── File permissions (Unix only) ───────────────────────────────────

    [Fact]
    public void Save_CreatesDirectory0700AndFile0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _env["HOME"] = _tempHome;
        var configDir = Path.Combine(_tempHome, ".config", "visual-relay");
        var configPath = Path.Combine(configDir, "obsidian.json");

        // Ensure clean state.
        if (Directory.Exists(configDir))
            Directory.Delete(configDir, recursive: true);

        ObsidianBridgeSettings.Save(
            new ObsidianBridgeConfig(false, "/vault", 60), _env);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(configDir));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(configPath));
    }

    // ── Non-macOS still functional ─────────────────────────────────────

    [Fact]
    public void Load_NonMacOs_StillReturnsFunctionalDefaults()
    {
        // On any platform, with HOME set, the bridge settings load without throwing.
        _env["HOME"] = _tempHome;

        var config = ObsidianBridgeSettings.Load(_env);

        Assert.NotNull(config);
        Assert.False(config.Enabled);
        Assert.Equal(60, config.PollSeconds);
        // Vault root still has the default iCloud path — the user can override.
    }

    // ── Overwrite existing file ────────────────────────────────────────

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        _env["HOME"] = _tempHome;

        // First save.
        ObsidianBridgeSettings.Save(
            new ObsidianBridgeConfig(true, "/first-path/", 30), _env);

        // Second save overwrites.
        ObsidianBridgeSettings.Save(
            new ObsidianBridgeConfig(false, "/second-path/", 120), _env);

        var loaded = ObsidianBridgeSettings.Load(_env);
        Assert.False(loaded.Enabled);
        Assert.Equal("/second-path/", loaded.VaultRoot);
        Assert.Equal(120, loaded.PollSeconds);
    }
}
