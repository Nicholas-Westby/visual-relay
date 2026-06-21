using System.Globalization;
using Avalonia.Data.Converters;
using VisualRelay.App.Services;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// Returns true when the bound <see cref="OutputFieldKind"/> equals the
/// binding's <c>ConverterParameter</c> string (case-sensitive).
/// </summary>
public class OutputFieldKindEqualsConverter : IValueConverter
{
    public static readonly OutputFieldKindEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is OutputFieldKind kind && parameter is string param)
            return kind.ToString() == param;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
