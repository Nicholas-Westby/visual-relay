using System.Globalization;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

/// <summary>
/// Pure helper that assigns per-day heading labels for a completion-ordered
/// archive list. Returns a label for the first row of each local-calendar day
/// and <c>null</c> for all subsequent rows on the same day.
/// </summary>
public static class ArchiveDayGrouping
{
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

        // Sum CostUsd across all tasks sharing the same local calendar day.
        var dayTotal = 0.0;
        foreach (var t in orderedTasks)
        {
            if (t.CompletedAt is { } ct)
            {
                var d = DateOnly.FromDateTime(ct.ToLocalTime().Date);
                if (d == localDay)
                    dayTotal += t.CostUsd;
            }
        }

        if (dayTotal > 0)
            heading = $"{heading} ({MoneyFormatter.Dollars(dayTotal)})";

        return heading;
    }
}
