using System.Globalization;

namespace VisualRelay.Core.Configuration;

/// <summary>
/// Append-only audit trail for Obsidian bridge settings writes. One line per
/// key change is appended to <c>settings-audit.log</c> next to the user-level
/// <c>.env</c> (same accessor-based path resolution). Values for
/// <c>VR_OBSIDIAN_*</c> keys are logged in clear; everything else is redacted
/// because the same <c>.env</c> stores API keys. Best-effort — never throws.
/// </summary>
public static class SettingsAuditLog
{
    private const string LogFileName = "settings-audit.log";
    private const long MaxSizeBytes = 64 * 1024;
    private const int TrimTailLines = 256;

    private static readonly string[] ClearKeys =
        ["VR_OBSIDIAN_ENABLED", "VR_OBSIDIAN_VAULT_ROOT", "VR_OBSIDIAN_POLL_SECONDS"];

    private static readonly Lock Gate = new();

    /// <summary>
    /// Appends one audit line for a single key change. <paramref name="oldValue"/>
    /// may be null (renders as <c>(unset)</c>). Values are logged in clear only
    /// for the <c>VR_OBSIDIAN_*</c> allowlist; everything else shows
    /// <c>&lt;redacted&gt;</c>. Best-effort — any failure is silently swallowed.
    /// </summary>
    public static void Append(
        string key,
        string? oldValue,
        string newValue,
        string source,
        IEnvironmentAccessor? accessor = null)
    {
        try
        {
            var logPath = ResolveLogPath(accessor);

            var oldDisplay = oldValue is null ? "(unset)" : FormatValue(key, oldValue);
            var newDisplay = FormatValue(key, newValue);

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ",
                CultureInfo.InvariantCulture);
            var pid = Environment.ProcessId;
            var proc = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            // Sanitize: replace newlines/carriage returns in values so an
            // attacker can't inject fake log lines via a crafted path.
            var line = $"{timestamp} {key} \"{Sanitize(oldDisplay)}\" -> " +
                       $"\"{Sanitize(newDisplay)}\" source={source} pid={pid} proc={proc}\n";

            EnsureDirectoryAndFile(logPath);

            lock (Gate)
            {
                File.AppendAllText(logPath, line);
                TrimIfTooLarge(logPath);
            }
        }
        catch
        {
            // Best-effort — never throw.
        }
    }

    private static string FormatValue(string key, string value) =>
        ClearKeys.Contains(key, StringComparer.Ordinal) ? value : "<redacted>";

    private static string Sanitize(string value) =>
        value.Replace("\r", "").Replace("\n", "");

    private static string ResolveLogPath(IEnvironmentAccessor? accessor)
    {
        var envPath = KeyEnvFile.ResolvePathForCurrentUser(accessor);
        var dir = Path.GetDirectoryName(envPath)!;
        return Path.Combine(dir, LogFileName);
    }

    private static void EnsureDirectoryAndFile(string logPath)
    {
        var dir = Path.GetDirectoryName(logPath);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        if (!File.Exists(logPath))
        {
            // Touch the file so permission hardening applies.
            File.WriteAllText(logPath, "");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(logPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        // Re-harden existing file.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(logPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TrimIfTooLarge(string logPath)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length <= MaxSizeBytes)
                return;

            var allLines = File.ReadAllLines(logPath);
            if (allLines.Length <= TrimTailLines)
                return;

            var tail = allLines[^TrimTailLines..];
            File.WriteAllText(logPath, string.Join("\n", tail) + "\n");
        }
        catch
        {
            // Best-effort trim — never throw.
        }
    }
}
