using System.Windows.Input;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels.RunLogRows;

/// <summary>
/// Display-layer row abstraction for the Run Log <c>ListBox</c>.
/// Each visible row is either a <see cref="SingleEventRow"/> wrapping
/// a single <see cref="RelayEvent"/>, or a <see cref="HeartbeatGroupRow"/>
/// collapsing a contiguous run of <c>watchdog_heartbeat</c> events.
/// </summary>
public interface IRunLogRow
{
    string DisplayLine { get; }
    string DetailLine { get; }
    bool IsAttention { get; }
    bool IsGroup { get; }
    int Count { get; }
    RelayEvent Event { get; }
    IReadOnlyList<RelayEvent> Members { get; }
    bool IsExpanded { get; set; }
    ICommand ToggleExpandCommand { get; }
}
