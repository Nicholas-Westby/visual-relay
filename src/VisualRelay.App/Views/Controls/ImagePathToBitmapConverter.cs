using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// Converts a filesystem path string to a <see cref="Bitmap"/> when the
/// path points to a file with a recognised image extension; returns null
/// for non-image files, missing files, or null/empty paths.
/// </summary>
public class ImagePathToBitmapConverter : IValueConverter
{
    public static readonly ImagePathToBitmapConverter Instance = new();

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
            return null;

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || !ImageExtensions.Contains(ext))
            return null;

        if (!File.Exists(path))
            return null;

        try
        {
            return new Bitmap(path);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value;
    }
}
