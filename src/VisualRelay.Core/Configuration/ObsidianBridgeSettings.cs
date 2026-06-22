using System.Text.Json;

namespace VisualRelay.Core.Configuration;

/// <summary>
/// Per-machine settings for the Obsidian task bridge. Settings are stored as
/// keys in the user-level <c>.env</c> file at
/// <c>$XDG_CONFIG_HOME/visual-relay/.env</c> (falling back to
/// <c>$HOME/.config/visual-relay/.env</c>) so the iCloud vault path stays out
/// of the in-repo <c>.relay/config.json</c> (which is shared with a VM).
///
/// Uses the <see cref="KeyEnvFile"/> infrastructure for XDG resolution,
/// <see cref="IEnvironmentAccessor"/> seam, and Unix permission hardening.
/// Migrates a legacy <c>obsidian.json</c> on first load.
/// </summary>
public static class ObsidianBridgeSettings
{
    /// <summary>
    /// Minimum allowed bridge poll interval (seconds). Enforced both on
    /// <see cref="Load"/> and at every live set (the VM property setter) so a
    /// value pushed via the settings UI or control API can't spin the timer too
    /// fast.
    /// </summary>
    public const int MinPollSeconds = 15;

    private static readonly string DefaultVaultRootTemplate =
        "~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/";

    // ── Load ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the bridge settings from the user-level <c>.env</c> file.
    /// Returns defaults when the file is missing or malformed.
    /// Migrates a legacy <c>obsidian.json</c> on first load.
    /// </summary>
    public static ObsidianBridgeConfig Load(IEnvironmentAccessor? accessor = null)
    {
        var home = KeyEnvFile.GetEnv("HOME", accessor);
        var defaultVaultRoot = ExpandDefaultVaultRoot(home);

        ObsidianBridgeConfig defaults = new(
            Enabled: false,
            VaultRoot: defaultVaultRoot,
            PollSeconds: 60);

        // If HOME is unset, we can't resolve a config path → return defaults.
        if (string.IsNullOrWhiteSpace(home))
        {
            return defaults;
        }

        try
        {
            // One-time migration from legacy obsidian.json (best-effort).
            TryMigrateFromObsidianJson(accessor);

            // Read the user-level .env file.
            var envDict = KeyEnvFile.Read(accessor);

            // Process-env-wins for each key: check the accessor/process env
            // first, then fall back to the .env file.
            var enabledStr = KeyEnvFile.GetEnv("VR_OBSIDIAN_ENABLED", accessor)
                ?? envDict.GetValueOrDefault("VR_OBSIDIAN_ENABLED");
            var vaultRootStr = KeyEnvFile.GetEnv("VR_OBSIDIAN_VAULT_ROOT", accessor)
                ?? envDict.GetValueOrDefault("VR_OBSIDIAN_VAULT_ROOT");
            var pollStr = KeyEnvFile.GetEnv("VR_OBSIDIAN_POLL_SECONDS", accessor)
                ?? envDict.GetValueOrDefault("VR_OBSIDIAN_POLL_SECONDS");

            // Parse with safe defaults.
            var enabled = bool.TryParse(enabledStr, out var e) && e;

            var vaultRoot = !string.IsNullOrWhiteSpace(vaultRootStr)
                ? ExpandTilde(vaultRootStr, home)
                : defaultVaultRoot;

            var pollSeconds = int.TryParse(pollStr, out var p) ? p : 60;
            if (pollSeconds < MinPollSeconds)
                pollSeconds = MinPollSeconds;

            return new ObsidianBridgeConfig(enabled, vaultRoot, pollSeconds);
        }
        catch (InvalidOperationException)
        {
            // Neither XDG_CONFIG_HOME nor HOME is set — can't resolve path.
            return defaults;
        }
    }

    // ── Save ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the bridge settings to the user-level <c>.env</c> file via
    /// three surgical <see cref="KeyEnvFile.Upsert"/> calls.
    /// <see cref="KeyEnvFile.Upsert"/> creates the parent directory with
    /// <c>0700</c> and the file with <c>0600</c>.
    /// </summary>
    public static void Save(ObsidianBridgeConfig settings, IEnvironmentAccessor? accessor = null)
    {
        try
        {
            KeyEnvFile.Upsert(
                "VR_OBSIDIAN_ENABLED",
                settings.Enabled ? "true" : "false",
                accessor);
            KeyEnvFile.Upsert(
                "VR_OBSIDIAN_VAULT_ROOT",
                settings.VaultRoot,
                accessor);
            KeyEnvFile.Upsert(
                "VR_OBSIDIAN_POLL_SECONDS",
                settings.PollSeconds.ToString(),
                accessor);
        }
        catch (InvalidOperationException)
        {
            // Nowhere to save — bail.
        }
    }

    // ── Migration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Best-effort migration from a legacy <c>obsidian.json</c> into the
    /// user-level <c>.env</c>. Only imports keys that are not already present
    /// in the <c>.env</c> file, then deletes <c>obsidian.json</c>.
    /// Swallows all exceptions — on failure the legacy file is left untouched.
    /// </summary>
    private static void TryMigrateFromObsidianJson(IEnvironmentAccessor? accessor)
    {
        try
        {
            var xdgConfigHome = KeyEnvFile.GetEnv("XDG_CONFIG_HOME", accessor);
            var home = KeyEnvFile.GetEnv("HOME", accessor);
            var configDir = XdgConfig.ResolveConfigDir(xdgConfigHome, home);
            var obsidianPath = Path.Combine(configDir, "visual-relay", "obsidian.json");

            if (!File.Exists(obsidianPath))
                return;

            var json = File.ReadAllText(obsidianPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Read current .env state so we only import unset keys.
            var envDict = KeyEnvFile.Read(accessor);

            if (!envDict.ContainsKey("VR_OBSIDIAN_ENABLED")
                && root.TryGetProperty("enabled", out var enabledProp))
            {
                if (enabledProp.ValueKind == JsonValueKind.True)
                    KeyEnvFile.Upsert("VR_OBSIDIAN_ENABLED", "true", accessor);
                else if (enabledProp.ValueKind == JsonValueKind.False)
                    KeyEnvFile.Upsert("VR_OBSIDIAN_ENABLED", "false", accessor);
            }

            if (!envDict.ContainsKey("VR_OBSIDIAN_VAULT_ROOT")
                && root.TryGetProperty("vaultRoot", out var vaultProp)
                && vaultProp.ValueKind == JsonValueKind.String)
            {
                var val = vaultProp.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                    KeyEnvFile.Upsert("VR_OBSIDIAN_VAULT_ROOT", val, accessor);
            }

            if (!envDict.ContainsKey("VR_OBSIDIAN_POLL_SECONDS")
                && root.TryGetProperty("pollSeconds", out var pollProp)
                && pollProp.ValueKind == JsonValueKind.Number
                && pollProp.TryGetInt32(out var pollVal))
            {
                KeyEnvFile.Upsert("VR_OBSIDIAN_POLL_SECONDS", pollVal.ToString(), accessor);
            }

            // Migration succeeded — delete the legacy file.
            File.Delete(obsidianPath);
        }
        catch
        {
            // Best-effort: leave obsidian.json on any failure.
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string ExpandDefaultVaultRoot(string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
        {
            // HOME unset → return the literal template (tests assert this).
            return DefaultVaultRootTemplate;
        }

        return ExpandTilde(DefaultVaultRootTemplate, home);
    }

    private static string ExpandTilde(string path, string home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return path;

        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(home, path[2..]);

        return path;
    }
}

/// <summary>
/// Per-machine Obsidian bridge configuration.
/// </summary>
public sealed record ObsidianBridgeConfig(
    bool Enabled,
    string VaultRoot,
    int PollSeconds);
