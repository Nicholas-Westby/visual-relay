using System.Globalization;
using Avalonia.Data.Converters;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// Multiplies a double value by a fraction passed as the converter
/// parameter.  When the parameter is omitted or not parseable the
/// default is <c>0.75</c>.
///
/// Used to bind <c>Image.MaxWidth</c> to
/// <c>ScrollViewer.Viewport.Width * 0.75</c> so inline image previews
/// consume 50–75 % of the available panel width.
/// </summary>
public class FractionConverter : IValueConverter
{
    public static readonly FractionConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double d)
            return null;

        var factor = 0.75;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            factor = parsed;

        return d * factor;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value;
    }
}
