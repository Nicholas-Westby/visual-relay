using System.Collections.ObjectModel;
using VisualRelay.App.ViewModels.RunLogRows;
using VisualRelay.Domain;
using static VisualRelay.Tests.RunLogGroupingTestHelpers;

namespace VisualRelay.Tests;

public sealed class RunLogGroupingRowTests
{
    // ── HeartbeatGroupRow expand/collapse ────────────────────────────────

    [Fact]
    public void ExpandCollapse_RoundTrips()
    {
        var group = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "msg3"),
            Heartbeat(7, "frontier", "msg2"),
            Heartbeat(7, "frontier", "msg1"));

        Assert.False(group.IsExpanded);
        Assert.Equal(3, group.Count);
        Assert.Equal(3, group.Members.Count);

        group.IsExpanded = true;
        Assert.True(group.IsExpanded);

        group.IsExpanded = false;
        Assert.False(group.IsExpanded);
    }

    [Fact]
    public void ExpandedGroup_MembersAreInNewestFirstOrder()
    {
        var h1 = Heartbeat(7, "frontier", "msg1");
        var h2 = Heartbeat(7, "frontier", "msg2");
        var h3 = Heartbeat(7, "frontier", "msg3");

        var group = HeartbeatGroupRow.Create(h3, h2, h1);

        Assert.Equal(3, group.Members.Count);
        Assert.Same(h3, group.Members[0]);
        Assert.Same(h2, group.Members[1]);
        Assert.Same(h1, group.Members[2]);
    }

    [Fact]
    public void ToggleExpandCommand_FlipsIsExpanded()
    {
        var group = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "msg2"),
            Heartbeat(7, "frontier", "msg1"));

        Assert.False(group.IsExpanded);
        Assert.NotNull(group.ToggleExpandCommand);

        group.ToggleExpandCommand.Execute(null);
        Assert.True(group.IsExpanded);

        group.ToggleExpandCommand.Execute(null);
        Assert.False(group.IsExpanded);
    }

    // ── HeartbeatGroupRow detail / display ───────────────────────────────

    [Fact]
    public void GroupRow_DisplayLine_IsSharedDisplayLine()
    {
        var group = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "msg3"),
            Heartbeat(7, "frontier", "msg2"));

        Assert.Equal("s7/frontier watchdog_heartbeat", group.DisplayLine);
    }

    [Fact]
    public void GroupRow_DetailLine_IsNewestMemberDetail()
    {
        var group = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "silenceMs=2000 deadlineMs=61000"),
            Heartbeat(7, "frontier", "silenceMs=1000 deadlineMs=62000"));

        Assert.Contains("silenceMs=2000", group.DetailLine);
        Assert.DoesNotContain("silenceMs=1000", group.DetailLine);
    }

    [Fact]
    public void GroupRow_IsNeverAttention()
    {
        var group = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "msg2"),
            Heartbeat(7, "frontier", "msg1"));

        Assert.False(group.IsAttention);
    }

    [Fact]
    public void GroupRow_Create_WithSingleEvent_ReturnsSingleEventRow()
    {
        var row = HeartbeatGroupRow.Create(Heartbeat(7, "frontier", "msg"));

        Assert.False(row.IsGroup);
        Assert.IsType<SingleEventRow>(row);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public void GroupRow_Create_WithMultipleEvents_ReturnsHeartbeatGroupRow()
    {
        var row = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "msg2"),
            Heartbeat(7, "frontier", "msg1"));

        Assert.True(row.IsGroup);
        Assert.IsType<HeartbeatGroupRow>(row);
        Assert.Equal(2, row.Count);
    }

    // ── SingleEventRow ───────────────────────────────────────────────────

    [Fact]
    public void SingleEventRow_DelegatesToRelayEvent()
    {
        var evt = StageStart(5, "balanced");
        var row = new SingleEventRow(evt);

        Assert.False(row.IsGroup);
        Assert.Same(evt, row.Event);
        Assert.Equal(evt.DisplayLine, row.DisplayLine);
        Assert.Equal(evt.DetailLine, row.DetailLine);
        Assert.Equal(evt.IsAttention, row.IsAttention);
        Assert.Equal(1, row.Count);
        Assert.Single(row.Members);
        Assert.Same(evt, row.Members[0]);
    }

    [Fact]
    public void SingleEventRow_WarnEvent_IsAttention()
    {
        var evt = new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "escalation", "run-1", "/root",
            "task-1", 7, "frontier");
        var row = new SingleEventRow(evt);

        Assert.True(row.IsAttention);
    }

    // ── ApplyLogFilter equivalence ───────────────────────────────────────

    [Fact]
    public void GroupEvents_AndIncrementalPath_ProduceSameResult()
    {
        var allEvents = new List<RelayEvent>
        {
            Heartbeat(7, "frontier", "msg3"),
            Heartbeat(7, "frontier", "msg2"),
            StageStart(7, "frontier"),
            Heartbeat(7, "frontier", "msg1"),
        };

        var grouped = RunLogGrouper.GroupEvents(allEvents);

        var incrementalRows = new ObservableCollection<IRunLogRow>();
        for (var idx = allEvents.Count - 1; idx >= 0; idx--)
        {
            var evt = allEvents[idx];
            if (incrementalRows.Count > 0 && RunLogGrouper.MergeNewest(incrementalRows, evt))
                continue;
            incrementalRows.Insert(0, new SingleEventRow(evt));
        }

        Assert.Equal(grouped.Count, incrementalRows.Count);
        for (var i = 0; i < grouped.Count; i++)
        {
            Assert.Equal(grouped[i].DisplayLine, incrementalRows[i].DisplayLine);
            Assert.Equal(grouped[i].IsGroup, incrementalRows[i].IsGroup);
            Assert.Equal(grouped[i].Count, incrementalRows[i].Count);
        }
    }

    [Fact]
    public void GroupEvents_AndIncrementalPath_WithTierChange_ProduceSameResult()
    {
        var allEvents = new List<RelayEvent>
        {
            Heartbeat(7, "balanced", "b2"),
            Heartbeat(7, "balanced", "b1"),
            Heartbeat(7, "frontier", "f2"),
            Heartbeat(7, "frontier", "f1"),
        };

        var grouped = RunLogGrouper.GroupEvents(allEvents);

        var incrementalRows = new ObservableCollection<IRunLogRow>();
        for (var idx = allEvents.Count - 1; idx >= 0; idx--)
        {
            var evt = allEvents[idx];
            if (incrementalRows.Count > 0 && RunLogGrouper.MergeNewest(incrementalRows, evt))
                continue;
            incrementalRows.Insert(0, new SingleEventRow(evt));
        }

        Assert.Equal(grouped.Count, incrementalRows.Count);
        for (var i = 0; i < grouped.Count; i++)
        {
            Assert.Equal(grouped[i].DisplayLine, incrementalRows[i].DisplayLine);
            Assert.Equal(grouped[i].IsGroup, incrementalRows[i].IsGroup);
            Assert.Equal(grouped[i].Count, incrementalRows[i].Count);
        }
    }
}
