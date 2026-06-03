using System.Globalization;

namespace VisualRelay.Domain;

public static class MoneyFormatter
{
    // Sub-cent amounts are shown with this many significant figures so that
    // real spend on cheap models never collapses to "$0.00".
    private const int SubCentSignificantFigures = 2;

    public static string Dollars(double usd)
    {
        if (usd <= 0)
        {
            return "$0.00";
        }

        if (usd >= 0.01)
        {
            var rounded = Math.Round(usd, 2, MidpointRounding.AwayFromZero);
            return $"${rounded.ToString("0.00", CultureInfo.InvariantCulture)}";
        }

        return $"${FormatSubCent(usd)}";
    }

    private static string FormatSubCent(double usd)
    {
        // Decimal places needed for the requested significant figures, e.g.
        // 0.0005 -> 4 places, 0.00051 -> 5 places, 0.0000012 -> 7 places.
        var magnitude = (int)Math.Floor(Math.Log10(usd));
        var decimals = Math.Min(15, SubCentSignificantFigures - 1 - magnitude);
        var rounded = Math.Round(usd, decimals, MidpointRounding.AwayFromZero);

        // Trim trailing zeros but keep at least two decimals (e.g. "$0.0005",
        // never "$0.0005000"), and never render as "$0.00".
        var text = rounded.ToString("0.00#############", CultureInfo.InvariantCulture);
        return text == "0.00" ? usd.ToString("0.00#############", CultureInfo.InvariantCulture) : text;
    }
}
