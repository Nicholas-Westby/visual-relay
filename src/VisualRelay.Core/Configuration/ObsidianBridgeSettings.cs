using System.Text.Json;

namespace VisualRelay.Core.Configuration;

/// <summary>
/// Per-machine settings for the Obsidian task bridge. Settings are stored at
/// <c>$XDG_CONFIG_HOME/visual-relay/obsidian.json</c> (falling back to
/// <c>$HOME/.config/visual-relay/obsidian.json</c>) so the iCloud vault path
/// stays out of the in-repo <c>.relay/config.json</c> (which is shared with a VM).
///
/// Mirrors the <see cref="KeyEnvFile"/> pattern: XDG resolution, an
/// <see cref="IEnvironmentAccessor"/> seam, and Unix permission hardening.
/// </summary>
public static class ObsidianBridgeSettings
{
    private const string DirName = "visual-relay";
    private const string FileName = "obsidian.json";
    private const int MinPollSeconds = 15;

    private static readonly string DefaultVaultRootTemplate =
        "~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    // ── Path resolution (testable) ───────────────────────────────────────

    /// <summary>
    /// Resolves the settings path given explicit directory overrides (for testability).
    /// Mirrors <see cref="KeyEnvFile.ResolvePath(string?,string?)"/>.
    /// </summary>
    internal static string ResolvePath(string? xdgConfigHome, string? home)
    {
        var configDir = XdgConfig.ResolveConfigDir(xdgConfigHome, home);
        return Path.Combine(configDir, DirName, FileName);
    }

    private static string ResolvePath(IEnvironmentAccessor? accessor = null) =>
        ResolvePath(
            KeyEnvFile.GetEnv("XDG_CONFIG_HOME", accessor),
            KeyEnvFile.GetEnv("HOME", accessor));

    // ── Load ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the bridge settings from the user-level XDG config file.
    /// Returns defaults when the file is missing or malformed.
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

        string filePath;
        try
        {
            filePath = ResolvePath(accessor);
        }
        catch (InvalidOperationException)
        {
            // Neither XDG_CONFIG_HOME nor HOME is set — can't resolve path.
            return defaults;
        }

        if (!File.Exists(filePath))
        {
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var loaded = JsonSerializer.Deserialize<ObsidianBridgeConfigDto>(json, JsonOptions);
            if (loaded is null)
            {
                return defaults;
            }

            // Expand ~ in vault root.
            var vaultRoot = loaded.VaultRoot ?? defaultVaultRoot;
            vaultRoot = ExpandTilde(vaultRoot, home);

            var pollSeconds = loaded.PollSeconds ?? 60;
            if (pollSeconds < MinPollSeconds)
                pollSeconds = MinPollSeconds;

            return new ObsidianBridgeConfig(
                Enabled: loaded.Enabled ?? false,
                VaultRoot: vaultRoot,
                PollSeconds: pollSeconds);
        }
        catch (JsonException)
        {
            return defaults;
        }
    }

    // ── Save ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the bridge settings to the user-level XDG config file.
    /// Creates the parent directory with <c>0700</c> and the file with <c>0600</c>.
    /// </summary>
    public static void Save(ObsidianBridgeConfig settings, IEnvironmentAccessor? accessor = null)
    {
        string filePath;
        try
        {
            filePath = ResolvePath(accessor);
        }
        catch (InvalidOperationException)
        {
            // Nowhere to save — bail.
            return;
        }

        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var dto = new ObsidianBridgeConfigDto
        {
            Enabled = settings.Enabled,
            VaultRoot = settings.VaultRoot,
            PollSeconds = settings.PollSeconds
        };
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(filePath, json);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    // ── DTO for serialisation ─────────────────────────────────────────────

    private sealed class ObsidianBridgeConfigDto
    {
        public bool? Enabled { get; set; }
        public string? VaultRoot { get; set; }
        public int? PollSeconds { get; set; }
    }
}

/// <summary>
/// Per-machine Obsidian bridge configuration.
/// </summary>
public sealed record ObsidianBridgeConfig(
    bool Enabled,
    string VaultRoot,
    int PollSeconds);
