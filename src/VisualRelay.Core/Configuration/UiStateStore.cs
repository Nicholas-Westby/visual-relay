using System.Text.Json;

namespace VisualRelay.Core.Configuration;

/// <summary>
/// Persisted UI layout state. Defaults match the design-time values in
/// <c>MainWindow.axaml</c>.
/// </summary>
public record UiState(double ActivityColumnWidth = 340, int ActivityTabIndex = 0);

/// <summary>
/// Reads and writes <c>ui-state.json</c> in the XDG config directory
/// (<c>$XDG_CONFIG_HOME/visual-relay/ui-state.json</c>, falling back to
/// <c>$HOME/.config/visual-relay/ui-state.json</c>).
///
/// All public methods are best-effort: they return defaults when the file
/// is missing or corrupt, and swallow all IO errors on write so the app
/// never crashes because of layout persistence.
/// </summary>
public static class UiStateStore
{
    private const string DirName = "visual-relay";
    private const string FileName = "ui-state.json";

    /// <summary>
    /// Returns defaults when the file is missing, corrupt, or the config
    /// directory cannot be resolved. Never throws.
    /// </summary>
    public static UiState Load(IEnvironmentAccessor? accessor = null)
    {
        try
        {
            var configDir = XdgConfig.ResolveConfigDir(accessor);
            var path = Path.Combine(configDir, DirName, FileName);
            if (!File.Exists(path))
                return new UiState();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UiState>(json) ?? new UiState();
        }
        catch
        {
            return new UiState();
        }
    }

    /// <summary>
    /// Writes <paramref name="state"/> to <c>ui-state.json</c>, creating
    /// the parent directory when needed. Swallows all exceptions — layout
    /// persistence is never allowed to crash the app.
    /// </summary>
    public static void Save(UiState state, IEnvironmentAccessor? accessor = null)
    {
        try
        {
            var configDir = XdgConfig.ResolveConfigDir(accessor);
            var dir = Path.Combine(configDir, DirName);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, FileName);
            var json = JsonSerializer.Serialize(state);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort: ignore all failures.
        }
    }
}
