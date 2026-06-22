using System.Runtime.InteropServices;
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

    /// <summary>
    /// Returns the resolved path to the user-level <c>.env</c> file for the
    /// current test's sandboxed HOME / XDG_CONFIG_HOME.
    /// </summary>
    private string EnvFilePath()
    {
        var configDir = XdgConfig.ResolveConfigDir(_env);
        return Path.Combine(configDir, "visual-relay", ".env");
    }

    // ── Load defaults ──────────────────────────────────────────────────

    [Fact]
    public void Load_NoEnvFile_ReturnsDefaults()
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

        // Write a .env with a too-low poll interval.
        KeyEnvFile.Upsert("VR_OBSIDIAN_ENABLED", "true", _env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_VAULT_ROOT", "/tmp/vault", _env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_POLL_SECONDS", "5", _env);

        var config = ObsidianBridgeSettings.Load(_env);

        // Must be clamped to the floor (≥ 15).
        Assert.True(config.PollSeconds >= 15);
    }

    [Fact]
    public void Load_ExpandsTildeInVaultRoot()
    {
        _env["HOME"] = _tempHome;

        KeyEnvFile.Upsert("VR_OBSIDIAN_ENABLED", "true", _env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_VAULT_ROOT",
            "~/Library/Mobile Documents/iCloud~md~obsidian/Documents/VR/", _env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_POLL_SECONDS", "60", _env);

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

    [Fact]
    public void SaveAndLoad_RoundTripsSpacesInVaultPath()
    {
        _env["HOME"] = _tempHome;

        // The default vault path contains spaces and ~md~ — it must survive
        // a round-trip through the .env file without added/removed quotes.
        var vaultPath = "/Users/test/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/";
        var original = new ObsidianBridgeConfig(
            Enabled: true,
            VaultRoot: vaultPath,
            PollSeconds: 60);

        ObsidianBridgeSettings.Save(original, _env);
        var loaded = ObsidianBridgeSettings.Load(_env);

        Assert.True(loaded.Enabled);
        Assert.Equal(vaultPath, loaded.VaultRoot);
        Assert.Equal(60, loaded.PollSeconds);

        // Also verify the raw .env line contains the path verbatim (no added quotes).
        var envRaw = File.ReadAllText(EnvFilePath());
        Assert.Contains("VR_OBSIDIAN_VAULT_ROOT=" + vaultPath, envRaw, StringComparison.Ordinal);
    }

    // ── Malformed values ───────────────────────────────────────────────

    [Fact]
    public void Load_MalformedValues_ReturnsDefaults()
    {
        _env["HOME"] = _tempHome;

        // Write non-bool Enabled and non-int PollSeconds.
        KeyEnvFile.Upsert("VR_OBSIDIAN_ENABLED", "not-a-bool", _env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_VAULT_ROOT", "/some/vault/", _env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_POLL_SECONDS", "not-an-int", _env);

        var config = ObsidianBridgeSettings.Load(_env);

        // Must degrade to safe defaults for the malformed fields.
        Assert.False(config.Enabled);
        Assert.Equal(60, config.PollSeconds);
        Assert.Equal("/some/vault/", config.VaultRoot);
    }

    // ── File permissions (Unix only) ───────────────────────────────────

    [Fact]
    public void Save_CreatesDir0700AndFile0600()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        _env["HOME"] = _tempHome;
        var configDir = Path.Combine(_tempHome, ".config", "visual-relay");
        var envPath = Path.Combine(configDir, ".env");

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
            File.GetUnixFileMode(envPath));
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

    // ── Overwrite existing keys ────────────────────────────────────────

    [Fact]
    public void Save_OverwritesExistingEnvKeys()
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

    // ── Process-env wins over file ─────────────────────────────────────

    [Fact]
    public void Load_ProcessEnvWinsOverFile()
    {
        _env["HOME"] = _tempHome;

        // Write values into the .env file.
        KeyEnvFile.Upsert("VR_OBSIDIAN_ENABLED", "false", _env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_VAULT_ROOT", "/file/vault/", _env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_POLL_SECONDS", "30", _env);

        // Set process-env overrides (must win).
        _env["VR_OBSIDIAN_ENABLED"] = "true";
        _env["VR_OBSIDIAN_VAULT_ROOT"] = "/env/vault/";
        _env["VR_OBSIDIAN_POLL_SECONDS"] = "90";

        var config = ObsidianBridgeSettings.Load(_env);

        Assert.True(config.Enabled);
        Assert.Equal("/env/vault/", config.VaultRoot);
        Assert.Equal(90, config.PollSeconds);
    }

}
