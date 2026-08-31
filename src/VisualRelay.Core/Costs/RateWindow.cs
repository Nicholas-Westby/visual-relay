namespace VisualRelay.Core.Costs;

/// <summary>
/// A recurring time window in a given timezone's local time.
/// Matching is [<see cref="StartLocal"/>, <see cref="EndLocal"/>) —
/// start inclusive, end exclusive. When the evaluation instant's local
/// time falls inside the window on a day <see cref="Days"/> covers, all four
/// rate components (Input, Output, CachedInput, CacheWrite) are multiplied by
/// <see cref="Multiplier"/>.
/// </summary>
/// <param name="StartLocal">Window start in the target timezone's local time (inclusive).</param>
/// <param name="EndLocal">Window end in the target timezone's local time (exclusive).</param>
/// <param name="TimeZoneId">IANA or Windows timezone ID, resolved via <see cref="System.TimeZoneInfo.FindSystemTimeZoneById"/>.</param>
/// <param name="Multiplier">Multiplier applied to all rate components when inside the window.</param>
/// <param name="Days">Days the window covers, read in <paramref name="TimeZoneId"/>.
/// Null means every day, so a provider that publishes no day restriction needs
/// no extra data.</param>
internal sealed record RateWindow(
    TimeOnly StartLocal,
    TimeOnly EndLocal,
    string TimeZoneId,
    double Multiplier,
    IReadOnlyList<DayOfWeek>? Days = null)
{
    /// <summary>Monday through Friday, for a schedule published as weekday-only.</summary>
    public static IReadOnlyList<DayOfWeek> Weekdays { get; } =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
    ];

    /// <summary>True when this window applies on <paramref name="day"/>.</summary>
    public bool MatchesDay(DayOfWeek day) => Days is null || Days.Contains(day);
}
