namespace VisualRelay.Core.Configuration;

/// <summary>
/// Shared XDG config-directory resolver. Extracted from
/// <see cref="KeyEnvFile"/> so consumers that need the config directory
/// (e.g. <c>UiStateStore</c>) can reuse the same logic without
/// importing secrets-handling code.
/// </summary>
public static class XdgConfig
{
    /// <summary>
    /// Resolves the XDG config directory from explicit environment values
    /// (for testability). Throws when neither is set.
    /// </summary>
    internal static string ResolveConfigDir(string? xdgConfigHome, string? home)
    {
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            return xdgConfigHome;
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".config");
        throw new InvalidOperationException(
            "Cannot resolve config directory: neither XDG_CONFIG_HOME nor HOME is set.");
    }

    /// <summary>
    /// Resolves the XDG config directory from <paramref name="accessor"/>
    /// (or the real process environment when null).
    /// </summary>
    public static string ResolveConfigDir(IEnvironmentAccessor? accessor = null) =>
        ResolveConfigDir(
            KeyEnvFile.GetEnv("XDG_CONFIG_HOME", accessor),
            KeyEnvFile.GetEnv("HOME", accessor));
}
