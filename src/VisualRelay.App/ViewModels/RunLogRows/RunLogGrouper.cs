using System.Collections.ObjectModel;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels.RunLogRows;

/// <summary>
/// Shared grouping logic consumed by both population paths
/// (incremental insert and <see cref="MainWindowViewModel.ApplyLogFilter"/> rebuild)
/// so live drains, stage-filter flips, and finished-task history group identically.
/// </summary>
public static class RunLogGrouper
{
    /// <summary>
    /// Convert a flat, newest-first sequence of <see cref="RelayEvent"/> into
    /// display rows. Consecutive <c>watchdog_heartbeat</c> events with identical
    /// <c>DisplayLine</c> collapse into a single <see cref="HeartbeatGroupRow"/>
    /// (or a <see cref="SingleEventRow"/> when the run length is 1).
    /// Every non-heartbeat event becomes a plain <see cref="SingleEventRow"/>.
    /// </summary>
    public static List<IRunLogRow> GroupEvents(IEnumerable<RelayEvent> events)
    {
        var rows = new List<IRunLogRow>();
        List<RelayEvent>? pendingHeartbeats = null;

        foreach (var evt in events)
        {
            if (evt.EventName == "watchdog_heartbeat")
            {
                if (pendingHeartbeats is { Count: > 0 } &&
                    pendingHeartbeats[^1].DisplayLine != evt.DisplayLine)
                {
                    FlushPendingHeartbeats(rows, ref pendingHeartbeats);
                }

                pendingHeartbeats ??= [];
                pendingHeartbeats.Add(evt);
            }
            else
            {
                FlushPendingHeartbeats(rows, ref pendingHeartbeats);
                rows.Add(new SingleEventRow(evt));
            }
        }

        FlushPendingHeartbeats(rows, ref pendingHeartbeats);
        return rows;
    }

    /// <summary>
    /// Attempt to merge <paramref name="relayEvent"/> into the newest row
    /// (index 0) of <paramref name="rows"/>. Returns <c>true</c> when the
    /// event was merged (either into an existing <see cref="HeartbeatGroupRow"/>
    /// or by promoting a lone-heartbeat <see cref="SingleEventRow"/> to a group).
    /// </summary>
    public static bool MergeNewest(
        ObservableCollection<IRunLogRow> rows,
        RelayEvent relayEvent)
    {
        if (relayEvent.EventName != "watchdog_heartbeat")
            return false;

        if (rows.Count == 0)
            return false;

        var newest = rows[0];

        // Case 1: newest row is a heartbeat group — check match and merge.
        if (newest is HeartbeatGroupRow group)
        {
            if (group.DisplayLine != relayEvent.DisplayLine)
                return false;

            group.InsertNewest(relayEvent);
            return true;
        }

        // Case 2: newest row is a single heartbeat — promote to group.
        if (newest is SingleEventRow { Event.EventName: "watchdog_heartbeat" } single &&
            single.DisplayLine == relayEvent.DisplayLine)
        {
            var promoted = HeartbeatGroupRow.FromList([relayEvent, single.Event]);
            promoted.IsExpanded = single.IsExpanded;
            rows[0] = promoted;
            return true;
        }

        return false;
    }

    private static void FlushPendingHeartbeats(
        List<IRunLogRow> rows,
        ref List<RelayEvent>? pending)
    {
        if (pending is not { Count: > 0 })
            return;

        rows.Add(HeartbeatGroupRow.Create(pending.ToArray()));
        pending = null;
    }
}
