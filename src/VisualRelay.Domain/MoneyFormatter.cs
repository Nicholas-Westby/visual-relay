using System.Globalization;

namespace VisualRelay.Domain;

public static class MoneyFormatter
{
    public static string Dollars(double usd)
    {
        var rounded = Math.Round(Math.Max(0, usd), 2, MidpointRounding.AwayFromZero);
        return $"${rounded.ToString("0.00", CultureInfo.InvariantCulture)}";
    }
}
