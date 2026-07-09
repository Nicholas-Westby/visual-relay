using System.Collections.ObjectModel;
using VisualRelay.App.ViewModels.RunLogRows;
using static VisualRelay.Tests.RunLogGroupingTestHelpers;

namespace VisualRelay.Tests;

public sealed class RunLogGroupingMergeTests
{
    // ── MergeNewest (live growth) tests ──────────────────────────────────

    [Fact]
    public void MergeNewest_IntoExistingGroup_IncrementsCountAndUpdatesDetail()
    {
        var rows = new ObservableCollection<IRunLogRow>();
        var group = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "silenceMs=60000 deadlineMs=120000"),
            Heartbeat(7, "frontier", "silenceMs=0 deadlineMs=60000"));
        rows.Add(group);
        Assert.Equal(2, group.Count);
        Assert.Contains("silenceMs=60000", group.DetailLine);

        var merged = RunLogGrouper.MergeNewest(
            rows,
            Heartbeat(7, "frontier", "silenceMs=2000 deadlineMs=62000"));

        Assert.True(merged);
        Assert.Equal(3, group.Count);
        Assert.Single(rows);
        Assert.Contains("silenceMs=2000", group.DetailLine);
        Assert.Contains("deadlineMs=62000", group.DetailLine);
    }

    [Fact]
    public void MergeNewest_IntoExistingGroup_PreservesExpandedState()
    {
        var rows = new ObservableCollection<IRunLogRow>();
        var group = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "msg2"),
            Heartbeat(7, "frontier", "msg1"));
        rows.Add(group);

        group.IsExpanded = true;
        Assert.True(group.IsExpanded);

        RunLogGrouper.MergeNewest(rows, Heartbeat(7, "frontier", "msg3"));

        Assert.True(group.IsExpanded,
            "Live count increment must not collapse an expanded group");
        Assert.Equal(3, group.Count);
    }

    [Fact]
    public void MergeNewest_DifferentTier_ReturnsFalse()
    {
        var rows = new ObservableCollection<IRunLogRow>();
        var group = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "msg2"),
            Heartbeat(7, "frontier", "msg1"));
        rows.Add(group);

        var merged = RunLogGrouper.MergeNewest(
            rows,
            Heartbeat(7, "balanced", "msg"));

        Assert.False(merged);
        Assert.Equal(2, group.Count);
    }

    [Fact]
    public void MergeNewest_DifferentStage_ReturnsFalse()
    {
        var rows = new ObservableCollection<IRunLogRow>();
        var group = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "msg2"),
            Heartbeat(7, "frontier", "msg1"));
        rows.Add(group);

        var merged = RunLogGrouper.MergeNewest(
            rows,
            Heartbeat(6, "frontier", "msg"));

        Assert.False(merged);
        Assert.Equal(2, group.Count);
    }

    [Fact]
    public void MergeNewest_NonHeartbeatEvent_ReturnsFalse()
    {
        var rows = new ObservableCollection<IRunLogRow>();
        var group = HeartbeatGroupRow.Create(
            Heartbeat(7, "frontier", "msg2"),
            Heartbeat(7, "frontier", "msg1"));
        rows.Add(group);

        var merged = RunLogGrouper.MergeNewest(
            rows,
            StageStart(7, "frontier"));

        Assert.False(merged);
        Assert.Equal(2, group.Count);
    }

    [Fact]
    public void MergeNewest_FirstRowIsSingleHeartbeat_PromotesToGroup()
    {
        var rows = new ObservableCollection<IRunLogRow>();
        var single = new SingleEventRow(Heartbeat(7, "frontier", "first"));
        rows.Add(single);

        var merged = RunLogGrouper.MergeNewest(
            rows,
            Heartbeat(7, "frontier", "second"));

        Assert.True(merged);
        Assert.Single(rows);
        Assert.True(rows[0].IsGroup);
        Assert.Equal(2, rows[0].Count);
    }

    [Fact]
    public void MergeNewest_FirstRowIsNonHeartbeat_ReturnsFalse()
    {
        var rows = new ObservableCollection<IRunLogRow>
        {
            new SingleEventRow(StageStart(7, "frontier"))
        };

        var merged = RunLogGrouper.MergeNewest(
            rows,
            Heartbeat(7, "frontier", "msg"));

        Assert.False(merged);
    }

    [Fact]
    public void MergeNewest_EmptyCollection_ReturnsFalse()
    {
        var rows = new ObservableCollection<IRunLogRow>();

        var merged = RunLogGrouper.MergeNewest(
            rows,
            Heartbeat(7, "frontier", "msg"));

        Assert.False(merged);
    }
}
