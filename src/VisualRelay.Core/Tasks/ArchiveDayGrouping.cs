using System.Globalization;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

/// <summary>
/// Pure helper that assigns per-day heading labels for a completion-ordered
/// archive list. Returns a label for the first row of each local-calendar day
/// and <c>null</c> for all subsequent rows on the same day. The newest
/// completed day's heading also carries rolling-30-day quick metrics (average
/// cost per task and total spend presented as a monthly rate).
/// </summary>
public static class ArchiveDayGrouping
{
    /// <summary>Rolling window (local calendar days, ending at and including
    /// <c>today</c>) feeding the newest header's per-task and per-month metrics.</summary>
    private const int MetricsWindowDays = 30;
    /// <summary>
    /// Returns the heading label for the row at <paramref name="index"/>,
    /// or <c>null</c> when it shares the same local day as the previous row.
    /// </summary>
    /// <param name="orderedTasks">Archive tasks ordered newest-completion-first.</param>
    /// <param name="index">Zero-based row index.</param>
    /// <param name="today">The reference "today" date (local).</param>
    public static string? HeadingFor(
        IReadOnlyList<RelayTaskItem> orderedTasks,
        int index,
        DateOnly today)
    {
        var task = orderedTasks[index];
        if (task.CompletedAt is not { } completedAt)
            return null;

        var localDay = DateOnly.FromDateTime(completedAt.ToLocalTime().Date);

        if (index > 0)
        {
            var prev = orderedTasks[index - 1];
            if (prev.CompletedAt is { } prevCompletedAt)
            {
                var prevDay = DateOnly.FromDateTime(prevCompletedAt.ToLocalTime().Date);
                if (prevDay == localDay)
                    return null;
            }
        }

        string heading;
        if (localDay == today)
            heading = "Today";
        else if (localDay == today.AddDays(-1))
            heading = "Yesterday";
        else
            heading = localDay.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);

        // One pass: this day's total, the rolling-window aggregates, and the newest
        // completed local day (the list arrives newest-first, but scanning is
        // order-independent and free at archive sizes).
        var dayTotal = 0.0;
        var windowTotal = 0.0;
        var windowCount = 0;
        DateOnly? newestDay = null;
        var windowStart = today.AddDays(-(MetricsWindowDays - 1));
        foreach (var t in orderedTasks)
        {
            if (t.CompletedAt is not { } ct)
                continue;
            var d = DateOnly.FromDateTime(ct.ToLocalTime().Date);
            if (newestDay is null || d > newestDay.Value)
                newestDay = d;
            if (d == localDay)
                dayTotal += t.CostUsd;
            if (d >= windowStart)
            {
                windowTotal += t.CostUsd;
                windowCount++;
            }
        }

        if (dayTotal > 0)
        {
            heading = $"{heading}: {MoneyFormatter.Dollars(dayTotal)}";

            // Quick metrics ride ONLY the newest group's header: average cost per
            // task and total spend over the rolling window, shown as a monthly rate.
            if (localDay == newestDay && windowCount > 0 && windowTotal > 0)
            {
                var perTask = MoneyFormatter.Dollars(windowTotal / windowCount);
                var perMonth = MoneyFormatter.WholeDollars(windowTotal);
                heading = $"{heading}, {perTask}/task, {perMonth}/mo";
            }
        }

        return heading;
    }
}
