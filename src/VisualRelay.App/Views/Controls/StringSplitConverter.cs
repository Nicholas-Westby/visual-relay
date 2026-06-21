using System.Globalization;
using Avalonia.Data.Converters;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// Splits a string by newlines into an <see cref="IEnumerable{String}"/>.
/// </summary>
public class StringSplitConverter : IValueConverter
{
    public static readonly StringSplitConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
            return s.Split('\n');
        return Array.Empty<string>();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
