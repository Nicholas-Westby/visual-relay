using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Views.Controls;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels.RunLogRows;

/// <summary>
/// A collapsed group of consecutive <c>watchdog_heartbeat</c> events
/// with identical <c>(StageNumber, Tier)</c>. Displays the shared
/// <see cref="DisplayLine"/> plus a count (e.g. "×30") and shows the
/// <b>newest</b> member's <see cref="DetailLine"/>.
/// </summary>
public partial class HeartbeatGroupRow : ObservableObject, IRunLogRow
{
    private readonly List<RelayEvent> _members;

    private HeartbeatGroupRow()
    {
        _members = [];
        Members = new ReadOnlyCollection<RelayEvent>(_members);
        ToggleExpandCommand = new RelayCommand(ToggleExpand);
    }

    /// <summary>
    /// Factory: returns a <see cref="SingleEventRow"/> when <paramref name="events"/>
    /// contains exactly one element, otherwise a <see cref="HeartbeatGroupRow"/>.
    /// Events must be newest-first and all share the same <c>DisplayLine</c>.
    /// </summary>
    public static IRunLogRow Create(params RelayEvent[] events)
    {
        if (events.Length == 1)
            return new SingleEventRow(events[0]);

        var row = new HeartbeatGroupRow();
        row._members.AddRange(events);
        row.Count = events.Length;
        return row;
    }

    /// <summary>
    /// Creates a group seeded with a list of events (newest-first).
    /// All events must share the same <c>DisplayLine</c>.
    /// </summary>
    public static HeartbeatGroupRow FromList(List<RelayEvent> events)
    {
        var row = new HeartbeatGroupRow();
        row._members.AddRange(events);
        row.Count = events.Count;
        return row;
    }

    public string DisplayLine => _members[0].DisplayLine;
    public string DetailLine => _members[0].DetailLine;
    public bool IsAttention => false;
    public bool IsGroup => true;
    public RelayEvent Event => _members[0];

    /// <summary>
    /// Chevron direction for the expand/collapse toggle:
    /// <see cref="ChevronDirection.Right"/> when collapsed,
    /// <see cref="ChevronDirection.Down"/> when expanded.
    /// </summary>
    public ChevronDirection ChevronDirection =>
        IsExpanded ? ChevronDirection.Down : ChevronDirection.Right;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isExpanded;

    public IReadOnlyList<RelayEvent> Members { get; }
    public ICommand ToggleExpandCommand { get; }

    /// <summary>
    /// Live-merge: prepend <paramref name="relayEvent"/> (the newest arrival)
    /// and increment the count. Does not collapse an expanded group.
    /// </summary>
    public void InsertNewest(RelayEvent relayEvent)
    {
        _members.Insert(0, relayEvent);
        Count = _members.Count;
        OnPropertyChanged(nameof(DisplayLine));
        OnPropertyChanged(nameof(DetailLine));
        OnPropertyChanged(nameof(Event));
    }

    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(ChevronDirection));
    }
}
