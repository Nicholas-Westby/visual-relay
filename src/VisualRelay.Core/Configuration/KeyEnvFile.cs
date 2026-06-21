using System.Text.RegularExpressions;

namespace VisualRelay.Core.Configuration;

/// <summary>
/// Source-of-truth helper for the user-level Visual Relay dotenv file at
/// <c>$XDG_CONFIG_HOME/visual-relay/.env</c> (falling back to
/// <c>$HOME/.config/visual-relay/.env</c>).
///
/// Resolution precedence: <b>process env &gt; user-level .env &gt; repo .env</b>
/// (the repo .env is a dev-only fallback and is handled in backend.sh, not here).
///
/// Environment accessor seam: every env-dependent method accepts an optional
/// <see cref="IEnvironmentAccessor"/> parameter. When null (default), the real
/// process environment is used. Tests inject a <c>DictionaryEnvironmentAccessor</c>
/// by passing it directly — no process-global static.
/// </summary>
public static class KeyEnvFile
{
    private const string DirName = "visual-relay";
    private const string FileName = ".env";

    // ── Environment accessor seam (testability) ──────────────────────────

    /// <summary>
    /// Reads an environment variable through <paramref name="accessor"/> when
    /// non-null, falling back to the real process environment.
    /// </summary>
    public static string? GetEnv(string name, IEnvironmentAccessor? accessor = null) =>
        accessor?.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(name);

    // ── Path resolution ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the user-level dotenv path, reading <c>XDG_CONFIG_HOME</c> and
    /// <c>HOME</c> from <paramref name="accessor"/> (or the real process env).
    /// </summary>
    private static string ResolvePath(IEnvironmentAccessor? accessor = null) =>
        ResolvePath(GetEnv("XDG_CONFIG_HOME", accessor), GetEnv("HOME", accessor));

    /// <summary>
    /// Resolves the user-level dotenv path given explicit directory overrides
    /// (for testability).
    /// </summary>
    internal static string ResolvePath(string? xdgConfigHome, string? home)
    {
        var configDir = XdgConfig.ResolveConfigDir(xdgConfigHome, home);
        return Path.Combine(configDir, DirName, FileName);
    }

    // ── Parse ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses <c>KEY=VALUE</c> lines from the resolved user-level dotenv,
    /// skipping blank lines and <c>#</c> comments. Returns an empty dictionary
    /// when the file does not exist.
    /// </summary>
    public static Dictionary<string, string> Read(IEnvironmentAccessor? accessor = null) =>
        Read(ResolvePath(accessor));

    /// <summary>
    /// Parses <c>KEY=VALUE</c> lines from <paramref name="filePath"/>,
    /// skipping blank lines and <c>#</c> comments. Returns an empty dictionary
    /// when the file does not exist.
    /// </summary>
    internal static Dictionary<string, string> Read(string filePath)
    {
        if (!File.Exists(filePath))
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>();
        foreach (var raw in File.ReadLines(filePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;

            var key = line[..eq].Trim();
            if (key.Length == 0)
                continue;

            var value = line[(eq + 1)..].Trim();

            // Strip surrounding single or double quotes (shell-style).
            if (value.Length >= 2)
            {
                if ((value[0] == '"' && value[^1] == '"') ||
                    (value[0] == '\'' && value[^1] == '\''))
                {
                    value = value[1..^1];
                }
            }

            result[key] = value;
        }

        return result;
    }

    // ── Upsert ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sets or replaces a single key in the resolved user-level dotenv,
    /// preserving all other lines byte-for-byte. Creates the parent directory
    /// with <c>0700</c> and the file with <c>0600</c> when they do not exist.
    /// </summary>
    public static void Upsert(string key, string value, IEnvironmentAccessor? accessor = null) =>
        Upsert(ResolvePath(accessor), key, value);

    /// <summary>
    /// Sets or replaces a single key in the dotenv file at
    /// <paramref name="filePath"/>, preserving all other lines byte-for-byte.
    /// Creates the parent directory with <c>0700</c> and the file with
    /// <c>0600</c> when they do not exist.
    /// </summary>
    internal static void Upsert(string filePath, string key, string value)
    {
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

        if (!File.Exists(filePath))
        {
            var content = key + "=" + value + Environment.NewLine;
            File.WriteAllText(filePath, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(filePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return;
        }

        // Ensure correct permissions on an existing file.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Read the raw file text to preserve original line endings and trailing
        // content byte-for-byte.
        var raw = File.ReadAllText(filePath);

        // Match a line whose trimmed key equals the target key. The pattern
        // captures leading whitespace (so we can reassemble the line with the
        // same indentation) and the rest of the line after the '=' so we can
        // replace only the value.  ^ and $ work with both \n and \r\n in
        // Multiline mode.
        var escapedKey = Regex.Escape(key);
        var pattern = @"^([ \t]*)" + escapedKey + @"([ \t]*=[ \t]*).*$";
        var match = Regex.Match(raw, pattern, RegexOptions.Multiline);
        if (match.Success)
        {
            // Replace the value portion while keeping leading whitespace and the
            // "=" separator intact.
            var replacement = match.Groups[1].Value + key + match.Groups[2].Value + value;
            raw = raw.Remove(match.Index, match.Length).Insert(match.Index, replacement);
        }
        else
        {
            // Key not present — append to the end, ensuring a preceding newline
            // when the file doesn't already end with one.
            if (raw.Length > 0 && raw[^1] != '\n')
                raw += Environment.NewLine;
            raw += key + "=" + value + Environment.NewLine;
        }

        File.WriteAllText(filePath, raw);
    }

    // ── GetUnsetKeys ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns keys and values from <paramref name="filePath"/> whose keys are
    /// <em>not</em> already set in the current process environment. This
    /// implements the "only-if-unset" guard so the process environment always
    /// wins over file values.
    /// </summary>
    internal static Dictionary<string, string> GetUnsetKeys(string filePath, IEnvironmentAccessor? accessor = null)
    {
        var all = Read(filePath);
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in all)
        {
            if (GetEnv(key, accessor) is null)
                result[key] = value;
        }

        return result;
    }
}
