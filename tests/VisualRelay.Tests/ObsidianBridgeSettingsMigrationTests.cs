using System.Text.Json;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Migration tests: verify that a legacy <c>obsidian.json</c> is imported
/// into the user-level <c>.env</c> on first load and then deleted.
/// Split from <see cref="ObsidianBridgeSettingsTests"/> to stay under the
/// 300-line guard.
/// </summary>
public sealed class ObsidianBridgeSettingsMigrationTests : IDisposable
{
    private readonly DictionaryEnvironmentAccessor _env = new();
    private readonly string _tempHome;

    public ObsidianBridgeSettingsMigrationTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "vr-obsidian-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
    }

    public void Dispose()
    {
        TestFileSystem.DeleteDirectoryResilient(_tempHome);
    }

    private string EnvFilePath()
    {
        var configDir = XdgConfig.ResolveConfigDir(_env);
        return Path.Combine(configDir, "visual-relay", ".env");
    }

    [Fact]
    public void Migration_ImportsObsidianJsonIntoEnv()
    {
        _env["HOME"] = _tempHome;

        // Write an obsidian.json as the old settings file.
        var configDir = XdgConfig.ResolveConfigDir(_env);
        var obsidianDir = Path.Combine(configDir, "visual-relay");
        var obsidianPath = Path.Combine(obsidianDir, "obsidian.json");
        Directory.CreateDirectory(obsidianDir);
        File.WriteAllText(obsidianPath, JsonSerializer.Serialize(new
        {
            enabled = true,
            vaultRoot = "/migrated/vault/path/",
            pollSeconds = 45
        }));

        // Load — the migration should import values into .env and delete obsidian.json.
        var config = ObsidianBridgeSettings.Load(_env);

        Assert.True(config.Enabled);
        Assert.Equal("/migrated/vault/path/", config.VaultRoot);
        Assert.Equal(45, config.PollSeconds);

        // obsidian.json must be deleted.
        Assert.False(File.Exists(obsidianPath));

        // The .env file must now contain the imported keys.
        var envRaw = File.ReadAllText(EnvFilePath());
        Assert.Contains("VR_OBSIDIAN_ENABLED=true", envRaw, StringComparison.Ordinal);
        Assert.Contains("VR_OBSIDIAN_VAULT_ROOT=/migrated/vault/path/", envRaw, StringComparison.Ordinal);
        Assert.Contains("VR_OBSIDIAN_POLL_SECONDS=45", envRaw, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_DoesNotOverwriteExistingEnvKeys()
    {
        _env["HOME"] = _tempHome;

        // Pre-populate .env with a vault root.
        KeyEnvFile.Upsert("VR_OBSIDIAN_VAULT_ROOT", "/existing/vault/", _env);

        // Write an obsidian.json with a different vault root.
        var configDir = XdgConfig.ResolveConfigDir(_env);
        var obsidianDir = Path.Combine(configDir, "visual-relay");
        var obsidianPath = Path.Combine(obsidianDir, "obsidian.json");
        Directory.CreateDirectory(obsidianDir);
        File.WriteAllText(obsidianPath, JsonSerializer.Serialize(new
        {
            enabled = true,
            vaultRoot = "/from-json/vault/",
            pollSeconds = 30
        }));

        // Load — migration must not overwrite the existing .env key.
        var config = ObsidianBridgeSettings.Load(_env);

        // Vault root should be the pre-existing .env value, not the json value.
        Assert.Equal("/existing/vault/", config.VaultRoot);

        // obsidian.json must still be deleted.
        Assert.False(File.Exists(obsidianPath));
    }
}
