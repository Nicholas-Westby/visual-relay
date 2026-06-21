using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// Negates a boolean value. Useful for binding <c>IsExpanded</c> to
/// <c>!CollapsedByDefault</c>.
/// </summary>
public class BoolNotConverter : IValueConverter
{
    public static readonly BoolNotConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return false;
    }
}
