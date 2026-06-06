namespace VisualRelay.Domain;

public static class ElapsedFormatter
{
    /// <summary>
    /// Formats a <see cref="TimeSpan"/> as a compact duration label like "7s",
    /// "1m 04s", or "2m 36s". Negative and sub-second spans return "0s".
    /// </summary>
    public static string Label(TimeSpan elapsed)
    {
        var seconds = Math.Floor(elapsed.TotalSeconds);
        if (seconds <= 0)
        {
            return "0s";
        }

        if (seconds < 60)
        {
            return $"{seconds:0}s";
        }

        var minutes = Math.Floor(seconds / 60);
        var remainder = seconds % 60;
        return $"{minutes:0}m {remainder:00}s";
    }
}
