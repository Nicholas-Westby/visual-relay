namespace VisualRelay.Core.Costs;

/// <summary>
/// A daily recurring time window in a given timezone's local time.
/// Matching is [<see cref="StartLocal"/>, <see cref="EndLocal"/>) —
/// start inclusive, end exclusive. When the evaluation instant's local
/// time falls inside the window, all four rate components (Input, Output,
/// CachedInput, CacheWrite) are multiplied by <see cref="Multiplier"/>.
/// </summary>
/// <param name="StartLocal">Window start in the target timezone's local time (inclusive).</param>
/// <param name="EndLocal">Window end in the target timezone's local time (exclusive).</param>
/// <param name="TimeZoneId">IANA or Windows timezone ID, resolved via <see cref="System.TimeZoneInfo.FindSystemTimeZoneById"/>.</param>
/// <param name="Multiplier">Multiplier applied to all rate components when inside the window.</param>
internal sealed record RateWindow(TimeOnly StartLocal, TimeOnly EndLocal, string TimeZoneId, double Multiplier);
