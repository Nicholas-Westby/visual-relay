using System.Text;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Loads the pre-devshell environment snapshot written by the
/// <c>visual-relay</c> bootstrap script. The file is produced by
/// <c>env -0</c> and is NUL-delimited KEY=value pairs.
/// Returns <c>null</c> when the snapshot is absent (packaged/brew installs,
/// or the env var is simply not set) — callers fall back to the current
/// process environment.
/// </summary>
internal static class UserEnvSnapshot
{
    /// <summary>
    /// Reads the snapshot file pointed at by
    /// <c>VISUAL_RELAY_USER_ENV_SNAPSHOT</c> and returns its contents as a
    /// dictionary, or <c>null</c> when the env var is absent or the file is
    /// missing/unreadable.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Load(IEnvironmentAccessor? accessor = null)
    {
        var path = KeyEnvFile.GetEnv("VISUAL_RELAY_USER_ENV_SNAPSHOT", accessor);
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            var dict = new Dictionary<string, string>();
            var start = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                    continue;
                var span = bytes.AsSpan(start, i - start);
                var entry = Encoding.UTF8.GetString(span);
                var eq = entry.IndexOf('=');
                if (eq >= 0)
                    dict[entry[..eq]] = entry[(eq + 1)..];
                start = i + 1;
            }
            return dict;
        }
        catch
        {
            return null;
        }
    }
}
