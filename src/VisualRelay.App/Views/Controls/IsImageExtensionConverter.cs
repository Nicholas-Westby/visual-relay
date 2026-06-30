using System.Globalization;
using Avalonia.Data.Converters;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// Returns <c>true</c> when the value is a path whose file extension is a
/// recognised image type (.png, .jpg, .jpeg, .gif, .bmp, .webp).
/// Used to control <c>IsVisible</c> on the inline image preview so that
/// non-image attachments do not render an empty Image element.
/// </summary>
public class IsImageExtensionConverter : IValueConverter
{
    public static readonly IsImageExtensionConverter Instance = new();

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
            return false;

        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value;
    }
}
