using VisualRelay.Core.Queue;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// Post-drain cleanup: drop the active-time accumulator for any task that is no
    /// longer running. A task LEFT IN THE PLANNED STATE (planned this drain but not
    /// executed — e.g. the drain paused or halted at a task boundary) would otherwise
    /// carry its planning-phase active time into the NEXT drain, where planning is
    /// skipped (stages 1–4 already Done) so <see cref="DrainLifecycleCallbacks.OnPlanningStarted"/>
    /// never re-fires and <see cref="BeginRunningTask"/>'s <c>TryAdd</c> PRESERVES the
    /// stale accumulator. Executed tasks already had theirs removed by
    /// <see cref="ClearRunningTask"/>, so the only leftovers are planned-but-unexecuted
    /// ones. Keyed by task id (independent of row rebuilds); idempotent.
    /// </summary>
    internal void DropStaleRunAnchorsAfterDrain()
    {
        var stale = _taskElapsed.Keys.Where(id => !_runningTaskIds.Contains(id)).ToList();
        foreach (var id in stale)
            _taskElapsed.Remove(id);
    }
}
