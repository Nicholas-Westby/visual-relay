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
    /// (for testability). On Windows, when neither XDG nor HOME is set, falls
    /// back to <c>%APPDATA%</c> (which the launcher/bash path never set);
    /// elsewhere it throws. XDG/HOME keep precedence on every OS so test
    /// injection and power-user overrides are preserved.
    /// </summary>
    internal static string ResolveConfigDir(string? xdgConfigHome, string? home) =>
        ResolveConfigDir(
            xdgConfigHome, home,
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>
    /// Pure resolver with the OS dispatch injected (<paramref name="isWindows"/>
    /// + <paramref name="appData"/>) so the Windows fallback is unit-testable on
    /// any OS. Throws only when no source resolves.
    /// </summary>
    internal static string ResolveConfigDir(
        string? xdgConfigHome, string? home, bool isWindows, string? appData)
    {
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            return xdgConfigHome;
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".config");
        if (isWindows && !string.IsNullOrWhiteSpace(appData))
            return appData;
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
