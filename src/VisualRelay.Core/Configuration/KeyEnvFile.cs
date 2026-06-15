using System.Text.RegularExpressions;

namespace VisualRelay.Core.Configuration;

/// <summary>
/// Source-of-truth helper for the user-level Visual Relay dotenv file at
/// <c>$XDG_CONFIG_HOME/visual-relay/.env</c> (falling back to
/// <c>$HOME/.config/visual-relay/.env</c>).
///
/// Resolution precedence: <b>process env &gt; user-level .env &gt; repo .env</b>
/// (the repo .env is a dev-only fallback and is handled in backend.sh, not here).
/// </summary>
public static class KeyEnvFile
{
    private const string DirName = "visual-relay";
    private const string FileName = ".env";

    // ── Environment accessor seam (testability) ──────────────────────────

    /// <summary>
    /// Scoped to the current async execution context so tests running in
    /// parallel under xUnit class-level parallelism never observe each
    /// other's fake accessor — eliminating the cross-test race that a
    /// plain static property would reintroduce.
    /// </summary>
    // ReSharper disable once InconsistentNaming — '_'-prefixed backing field that
    // intentionally mirrors the public EnvironmentAccessorOverride property; the
    // PascalCase the rule wants would collide with that property name.
    private static readonly AsyncLocal<IEnvironmentAccessor?> _environmentAccessorOverride = new();

    /// <summary>
    /// When set, all environment reads route through this accessor instead of
    /// the real process environment. Tests set a <c>DictionaryEnvironmentAccessor</c>
    /// to eliminate process-global mutation races under parallel execution.
    /// Reset to <c>null</c> in test dispose to restore real-env behaviour.
    /// </summary>
    public static IEnvironmentAccessor? EnvironmentAccessorOverride
    {
        get => _environmentAccessorOverride.Value;
        set => _environmentAccessorOverride.Value = value;
    }

    /// <summary>
    /// Reads an environment variable through <see cref="EnvironmentAccessorOverride"/>
    /// when set, falling back to the real process environment.
    /// </summary>
    public static string? GetEnv(string name) =>
        EnvironmentAccessorOverride?.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(name);

    // ── Path resolution ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the user-level dotenv path, reading <c>XDG_CONFIG_HOME</c> and
    /// <c>HOME</c> from the environment accessor seam.
    /// </summary>
    private static string ResolvePath() =>
        ResolvePath(GetEnv("XDG_CONFIG_HOME"), GetEnv("HOME"));

    /// <summary>
    /// Resolves the user-level dotenv path given explicit directory overrides
    /// (for testability).
    /// </summary>
    internal static string ResolvePath(string? xdgConfigHome, string? home)
    {
        var configDir = !string.IsNullOrWhiteSpace(xdgConfigHome)
            ? xdgConfigHome
            : !string.IsNullOrWhiteSpace(home)
                ? Path.Combine(home, ".config")
                : throw new InvalidOperationException(
                    "Cannot resolve KeyEnvFile path: neither XDG_CONFIG_HOME nor HOME is set.");

        return Path.Combine(configDir, DirName, FileName);
    }

    // ── Parse ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses <c>KEY=VALUE</c> lines from the resolved user-level dotenv,
    /// skipping blank lines and <c>#</c> comments. Returns an empty dictionary
    /// when the file does not exist.
    /// </summary>
    public static Dictionary<string, string> Read() => Read(ResolvePath());

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
    public static void Upsert(string key, string value) => Upsert(ResolvePath(), key, value);

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
    /// Returns keys and values from the resolved user-level dotenv whose keys
    /// are <em>not</em> already set in the current process environment. This
    /// implements the "only-if-unset" guard so the process environment always
    /// wins over file values.
    /// </summary>
    // ReSharper disable once UnusedMember.Global — public-API default-path overload,
    // symmetric with Read()/Upsert(); the path-taking core is exercised by tests.
    public static Dictionary<string, string> GetUnsetKeys() => GetUnsetKeys(ResolvePath());

    /// <summary>
    /// Returns keys and values from <paramref name="filePath"/> whose keys are
    /// <em>not</em> already set in the current process environment. This
    /// implements the "only-if-unset" guard so the process environment always
    /// wins over file values.
    /// </summary>
    internal static Dictionary<string, string> GetUnsetKeys(string filePath)
    {
        var all = Read(filePath);
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in all)
        {
            if (GetEnv(key) is null)
                result[key] = value;
        }

        return result;
    }
}
