using System;
using System.Globalization;
using Avalonia.Data.Converters;
using VisualRelay.App.ViewModels;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// Returns true when the bound <see cref="StageDetailState"/> equals the
/// <see cref="ConverterParameter"/> string (case-sensitive).
/// </summary>
public class StageStateEqualsConverter : IValueConverter
{
    public static readonly StageStateEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is StageDetailState state && parameter is string param)
            return state.ToString() == param;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
