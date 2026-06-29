using System.Globalization;
using Avalonia.Data.Converters;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// Replaces the user's home directory prefix with <c>~</c> for GUI display.
/// In-memory paths are kept unchanged; the conversion only affects rendered text.
/// </summary>
public class HomePathToTildeConverter : IValueConverter
{
    public static readonly HomePathToTildeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
            return value;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return value;

        // Normalize: strip any trailing directory separators from the home path
        // so we can do a clean prefix match.
        var homeTrimmed = home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Must start with the home directory (case-sensitive on Unix, case-insensitive on Windows).
        if (!path.StartsWith(homeTrimmed, StringComparison.Ordinal))
            return value;

        // Exact match → just the tilde.
        if (path.Length == homeTrimmed.Length)
            return "~";

        // The character immediately after the home prefix must be a directory separator;
        // otherwise we'd false-match a sibling directory like /Users/nicholaswestby-extra.
        var next = path[homeTrimmed.Length];
        if (next != Path.DirectorySeparatorChar && next != Path.AltDirectorySeparatorChar)
            return value;

        // Replace the home prefix with '~' and keep the rest of the path intact.
        return "~" + path.Substring(homeTrimmed.Length);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value;
    }
}
