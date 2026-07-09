using VisualRelay.App.ViewModels.RunLogRows;
using VisualRelay.Domain;
using static VisualRelay.Tests.RunLogGroupingTestHelpers;

namespace VisualRelay.Tests;

public sealed class RunLogGroupingTests
{
    // ── GroupEvents tests ────────────────────────────────────────────────

    [Fact]
    public void ConsecutiveMatchingHeartbeats_CollapseToSingleGroup()
    {
        var events = new[]
        {
            Heartbeat(7, "frontier", "silenceMs=1000 deadlineMs=62000"),
            Heartbeat(7, "frontier", "silenceMs=2000 deadlineMs=61000"),
            Heartbeat(7, "frontier", "silenceMs=3000 deadlineMs=60000"),
        };

        var rows = RunLogGrouper.GroupEvents(events);

        var group = Assert.Single(rows);
        Assert.True(group.IsGroup);
        Assert.Equal(3, group.Count);
        Assert.Equal("s7/frontier watchdog_heartbeat", group.DisplayLine);
        // Detail = newest member's detail (the first in newest-first order)
        Assert.Contains("silenceMs=1000", group.DetailLine);
        Assert.Contains("deadlineMs=62000", group.DetailLine);
    }

    [Fact]
    public void InterleavedNonHeartbeat_SplitsGroups()
    {
        var events = new[]
        {
            Heartbeat(7, "frontier", "msg3"),
            StageStart(7, "frontier"),
            Heartbeat(7, "frontier", "msg2"),
            Heartbeat(7, "frontier", "msg1"),
        };

        var rows = RunLogGrouper.GroupEvents(events);

        Assert.Equal(3, rows.Count);
        // First row: single heartbeat (before stage_start split)
        Assert.False(rows[0].IsGroup);
        Assert.Equal("watchdog_heartbeat", rows[0].Event.EventName);
        // Second row: stage_start (plain, non-heartbeat)
        Assert.False(rows[1].IsGroup);
        Assert.Equal("stage_start", rows[1].Event.EventName);
        // Third row: group of 2 heartbeats (after stage_start)
        Assert.True(rows[2].IsGroup);
        Assert.Equal(2, rows[2].Count);
        Assert.Equal("s7/frontier watchdog_heartbeat", rows[2].DisplayLine);
    }

    [Fact]
    public void TierChange_StartsNewGroup()
    {
        var events = new[]
        {
            Heartbeat(7, "frontier", "f3"),
            Heartbeat(7, "frontier", "f2"),
            Heartbeat(7, "balanced", "b2"),
            Heartbeat(7, "balanced", "b1"),
        };

        var rows = RunLogGrouper.GroupEvents(events);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].IsGroup);
        Assert.Equal(2, rows[0].Count);
        Assert.Equal("s7/frontier watchdog_heartbeat", rows[0].DisplayLine);
        Assert.True(rows[1].IsGroup);
        Assert.Equal(2, rows[1].Count);
        Assert.Equal("s7/balanced watchdog_heartbeat", rows[1].DisplayLine);
    }

    [Fact]
    public void StageNumberChange_StartsNewGroup()
    {
        var events = new[]
        {
            Heartbeat(6, "balanced", "s6b"),
            Heartbeat(7, "frontier", "s7f2"),
            Heartbeat(7, "frontier", "s7f1"),
        };

        var rows = RunLogGrouper.GroupEvents(events);

        Assert.Equal(2, rows.Count);
        // First row: single s6 heartbeat (no group because only one)
        Assert.False(rows[0].IsGroup);
        Assert.Equal("s6/balanced watchdog_heartbeat", rows[0].DisplayLine);
        // Second row: group of 2 s7 heartbeats
        Assert.True(rows[1].IsGroup);
        Assert.Equal(2, rows[1].Count);
        Assert.Equal("s7/frontier watchdog_heartbeat", rows[1].DisplayLine);
    }

    [Fact]
    public void SingleHeartbeat_RendersAsPlainRow()
    {
        var events = new[] { Heartbeat(7, "frontier", "msg") };

        var rows = RunLogGrouper.GroupEvents(events);

        var row = Assert.Single(rows);
        Assert.False(row.IsGroup);
        Assert.Equal("s7/frontier watchdog_heartbeat", row.DisplayLine);
        Assert.Equal(1, row.Count);
        Assert.Equal("watchdog_heartbeat", row.Event.EventName);
    }

    [Fact]
    public void NonHeartbeatEvents_AreByteIdenticalToToday()
    {
        var evt = StageStart(5, "balanced");

        var rows = RunLogGrouper.GroupEvents([evt]);

        var row = Assert.Single(rows);
        Assert.False(row.IsGroup);
        Assert.Same(evt, row.Event);
        Assert.Equal(evt.DisplayLine, row.DisplayLine);
        Assert.Equal(evt.DetailLine, row.DetailLine);
        Assert.Equal(evt.IsAttention, row.IsAttention);
    }

    [Fact]
    public void WarnEvent_NeverSwallowedIntoGroup()
    {
        // Heartbeats are always "debug" level. Verify that a "warn" event
        // cannot be absorbed — the grouping rule is keyed on EventName, not
        // Level, and warn/error events are never "watchdog_heartbeat".
        var evt = new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "escalation", "run-1", "/root",
            "task-1", 7, "frontier");

        var rows = RunLogGrouper.GroupEvents([evt]);

        var row = Assert.Single(rows);
        Assert.False(row.IsGroup);
        Assert.Equal("escalation", row.Event.EventName);
        Assert.True(row.IsAttention);
    }

    [Fact]
    public void TwoHeartbeatsSeparatedByNonHeartbeat_AreTwoSeparateUngroupedRows()
    {
        var events = new[]
        {
            Heartbeat(7, "frontier", "second"),
            Trace(7, "frontier", "trace title", "trace content"),
            Heartbeat(7, "frontier", "first"),
        };

        var rows = RunLogGrouper.GroupEvents(events);

        Assert.Equal(3, rows.Count);
        // Each heartbeat is a single, ungrouped row because the trace splits them
        Assert.False(rows[0].IsGroup);
        Assert.Equal("watchdog_heartbeat", rows[0].Event.EventName);
        Assert.False(rows[1].IsGroup);
        Assert.Equal("trace", rows[1].Event.EventName);
        Assert.False(rows[2].IsGroup);
        Assert.Equal("watchdog_heartbeat", rows[2].Event.EventName);
    }

    [Fact]
    public void GroupEvents_EmptyInput_ReturnsEmpty()
    {
        var rows = RunLogGrouper.GroupEvents([]);
        Assert.Empty(rows);
    }

    [Fact]
    public void GroupEvents_SingleNonHeartbeat_ReturnsSingleRow()
    {
        var evt = StageStart(1, "cheap");
        var rows = RunLogGrouper.GroupEvents([evt]);
        var row = Assert.Single(rows);
        Assert.False(row.IsGroup);
        Assert.Same(evt, row.Event);
    }

    [Fact]
    public void GroupEvents_PreservesNewestFirstOrder()
    {
        var first = Heartbeat(7, "frontier", "first");
        var second = Heartbeat(7, "frontier", "second");
        var third = Heartbeat(7, "frontier", "third");

        var rows = RunLogGrouper.GroupEvents([third, second, first]);

        var group = Assert.Single(rows);
        Assert.Equal(3, group.Count);
        Assert.Same(third, group.Event); // newest = first in input
        Assert.Same(third, group.Members[0]);
        Assert.Same(second, group.Members[1]);
        Assert.Same(first, group.Members[2]);
    }
}
