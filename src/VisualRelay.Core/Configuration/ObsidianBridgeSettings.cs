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
    public static ObsidianBridgeConfig Load(IEnvironmentAccessor? accessor = null) =>
        Load(accessor, useIcloudDefault: !OperatingSystem.IsWindows());

    /// <summary>
    /// As <see cref="Load(IEnvironmentAccessor?)"/> with the default-vault decision
    /// injected so it is testable on any OS. The iCloud template is the default on
    /// macOS and Linux (unchanged from before this Windows port); only on Windows —
    /// where the iCloud path is dead — is the default empty so no dead path surfaces,
    /// and the user supplies a vault path if they enable the bridge.
    /// </summary>
    internal static ObsidianBridgeConfig Load(IEnvironmentAccessor? accessor, bool useIcloudDefault)
    {
        var home = KeyEnvFile.GetEnv("HOME", accessor);
        var defaultVaultRoot = ExpandDefaultVaultRoot(home, useIcloudDefault);

        ObsidianBridgeConfig defaults = new(
            Enabled: false,
            VaultRoot: defaultVaultRoot,
            PollSeconds: 60);

        // If HOME is unset, we can't resolve a config path → return defaults.
        // This early return is also why no InvalidOperationException guard is
        // needed below: with HOME set, path resolution can never throw.
        if (string.IsNullOrWhiteSpace(home))
        {
            return defaults;
        }

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
            ? TildePath.Expand(vaultRootStr, home)
            : defaultVaultRoot;

        var pollSeconds = int.TryParse(pollStr, out var p) ? p : 60;
        if (pollSeconds < MinPollSeconds)
            pollSeconds = MinPollSeconds;

        return new ObsidianBridgeConfig(enabled, vaultRoot, pollSeconds);
    }

    // ── Save ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the bridge settings to the user-level <c>.env</c> file via
    /// surgical <see cref="KeyEnvFile.Upsert"/> calls — but only for keys
    /// whose values actually changed (dirty-checked against the current file
    /// contents). Each changed key also appends an audit line via
    /// <see cref="SettingsAuditLog"/>.
    /// </summary>
    public static void Save(ObsidianBridgeConfig settings, IEnvironmentAccessor? accessor = null,
        string source = "settings-ui")
    {
        try
        {
            // Read current .env values for dirty-check and audit old→new.
            Dictionary<string, string> current;
            try
            {
                current = KeyEnvFile.Read(accessor);
            }
            catch (InvalidOperationException)
            {
                // Can't read — treat as empty (all keys are new).
                current = new Dictionary<string, string>();
            }

            UpsertIfChanged("VR_OBSIDIAN_ENABLED",
                settings.Enabled ? "true" : "false",
                current.GetValueOrDefault("VR_OBSIDIAN_ENABLED"),
                source, accessor);
            UpsertIfChanged("VR_OBSIDIAN_VAULT_ROOT",
                settings.VaultRoot,
                current.GetValueOrDefault("VR_OBSIDIAN_VAULT_ROOT"),
                source, accessor);
            UpsertIfChanged("VR_OBSIDIAN_POLL_SECONDS",
                settings.PollSeconds.ToString(),
                current.GetValueOrDefault("VR_OBSIDIAN_POLL_SECONDS"),
                source, accessor);
        }
        catch (InvalidOperationException)
        {
            // Nowhere to save — bail.
        }
    }

    private static void UpsertIfChanged(string key, string newValue, string? currentValue,
        string source, IEnvironmentAccessor? accessor)
    {
        if (string.Equals(newValue, currentValue, StringComparison.Ordinal))
            return;

        KeyEnvFile.Upsert(key, newValue, accessor);
        SettingsAuditLog.Append(key, currentValue, newValue, source, accessor);
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
                {
                    KeyEnvFile.Upsert("VR_OBSIDIAN_ENABLED", "true", accessor);
                    SettingsAuditLog.Append("VR_OBSIDIAN_ENABLED", null, "true",
                        "migration", accessor);
                }
                else if (enabledProp.ValueKind == JsonValueKind.False)
                {
                    KeyEnvFile.Upsert("VR_OBSIDIAN_ENABLED", "false", accessor);
                    SettingsAuditLog.Append("VR_OBSIDIAN_ENABLED", null, "false",
                        "migration", accessor);
                }
            }

            if (!envDict.ContainsKey("VR_OBSIDIAN_VAULT_ROOT")
                && root.TryGetProperty("vaultRoot", out var vaultProp)
                && vaultProp.ValueKind == JsonValueKind.String)
            {
                var val = vaultProp.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    KeyEnvFile.Upsert("VR_OBSIDIAN_VAULT_ROOT", val, accessor);
                    SettingsAuditLog.Append("VR_OBSIDIAN_VAULT_ROOT", null, val,
                        "migration", accessor);
                }
            }

            if (!envDict.ContainsKey("VR_OBSIDIAN_POLL_SECONDS")
                && root.TryGetProperty("pollSeconds", out var pollProp)
                && pollProp.ValueKind == JsonValueKind.Number
                && pollProp.TryGetInt32(out var pollVal))
            {
                KeyEnvFile.Upsert("VR_OBSIDIAN_POLL_SECONDS", pollVal.ToString(), accessor);
                SettingsAuditLog.Append("VR_OBSIDIAN_POLL_SECONDS", null,
                    pollVal.ToString(), "migration", accessor);
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

    private static string ExpandDefaultVaultRoot(string? home, bool useIcloudDefault)
    {
        // macOS/Linux keep the iCloud template default (unchanged); only Windows,
        // where the iCloud path is dead, surfaces nothing rather than a dead path.
        if (!useIcloudDefault)
            return string.Empty;

        if (string.IsNullOrWhiteSpace(home))
        {
            // HOME unset → return the literal template (tests assert this).
            return DefaultVaultRootTemplate;
        }

        return TildePath.Expand(DefaultVaultRootTemplate, home);
    }
}

/// <summary>
/// Per-machine Obsidian bridge configuration.
/// </summary>
public sealed record ObsidianBridgeConfig(
    bool Enabled,
    string VaultRoot,
    int PollSeconds);
