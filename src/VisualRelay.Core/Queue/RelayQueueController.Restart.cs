using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Queue;

public sealed partial class RelayQueueController
{
    /// <summary>
    /// When non-null, the drain will invoke this callback with the handoff
    /// record whenever it stops after a committed task in RestartBetweenTasks
    /// mode. The GUI wires this to trigger the relaunch + shutdown sequence.
    /// </summary>
    public Action<RestartHandoff>? OnRestartRequested { get; set; }

    /// <summary>
    /// Collects new tasks that appeared in <see cref="Tasks"/> since the
    /// drain started and merges them into the execution queue — the same
    /// logic shared by Sequential and RestartBetweenTasks at each task
    /// boundary.
    /// </summary>
    private List<RelayTaskItem> CollectAndMergeNewTasksAtBoundary(
        HashSet<string> seenIds,
        List<RelayTaskItem> queue)
    {
        SyncExternalTasks();
        var newTasks = CollectNewTasks(Tasks, seenIds);
        if (newTasks.Count > 0)
        {
            foreach (var nt in newTasks) seenIds.Add(nt.Id);
            return MergeNewTasksIntoQueue(queue, newTasks, Tasks);
        }
        return queue;
    }

    /// <summary>
    /// RestartBetweenTasks boundary: after a committed task, write the
    /// handoff, log the event, and signal the GUI to trigger the relaunch.
    /// Returns true when the drain must stop for a restart.
    /// </summary>
    private bool TryRestartBetweenTasks(
        RunAllMode mode, RelayTaskOutcome outcome, string taskId,
        string drainRunId, int pendingCount, List<RelayTaskOutcome> results)
    {
        if (mode != RunAllMode.RestartBetweenTasks
            || outcome.Status != RelayTaskOutcomeStatus.Committed)
            return false;

        DrainSummaryLog.Write(RootPath, drainRunId, taskId, "execute",
            "restart-handoff", $"pending={pendingCount}");
        var handoff = RestartHandoff.Write(RootPath, outcome, drainRunId, pendingCount);
        OnRestartRequested?.Invoke(handoff);
        State = RelayQueueState.Completed;
        return true;
    }

    /// <summary>
    /// No-progress guard: when a RestartBetweenTasks cycle produces zero
    /// committed outcomes, consume any prior-launch handoff so the next
    /// launch won't auto-resume into an empty queue.
    /// </summary>
    private void ConsumeHandoffIfRestartMode(RunAllMode mode)
    {
        if (mode == RunAllMode.RestartBetweenTasks)
            RestartHandoff.MarkConsumed(RootPath);
    }
}
