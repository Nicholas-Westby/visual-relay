using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Queue;

// ReSharper disable once UnusedType.Global — partial of RelayQueueController
public sealed partial class RelayQueueController
{
    private bool StagesOneThroughFourAreDone(string taskId)
    {
        var status = StageStatusRecord.Read(Path.Combine(RootPath, ".relay", taskId));
        return status.Count >= 4 && status.Take(4).All(e => e.Status == "Done");
    }

    private void WriteNeedsReviewMarker(string taskId, string reason)
    {
        var dir = Path.Combine(RootPath, ".relay", taskId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "NEEDS-REVIEW"), reason + Environment.NewLine);
    }

    private async Task ResetAndLogAsync(string taskId, string? tasksDir, string drainRunId, string phase, CancellationToken ct)
    {
        var gitInvoker = new GitInvoker();
        try { await WorktreeResetter.ResetAsync(RootPath, taskId, tasksDir, ct, gitInvoker); }
        catch (Exception ex) { DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase, "reset-failed", ex.Message); }
    }

    private int IndexOf(string taskId)
    {
        for (var i = 0; i < Tasks.Count; i++)
            if (string.Equals(Tasks[i].Id, taskId, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// Sets an optional external source of task items that the drain loop will
    /// pull into <see cref="Tasks"/> before each new-task checkpoint. This is
    /// the bridge that lets the GUI push newly-created tasks into the running
    /// controller without giving the controller a reference to the ViewModel.
    /// </summary>
    public void SetExternalTaskSource(Func<IReadOnlyList<RelayTaskItem>>? source)
    {
        _externalTaskSource = source;
    }

    /// <summary>
    /// Pulls task items from the external source (when set) and adds any that
    /// are not already present in <see cref="Tasks"/> by id. Called before each
    /// <see cref="CollectNewTasks"/> checkpoint so newly-created GUI tasks
    /// become visible to the running drain.
    /// </summary>
    private void SyncExternalTasks()
    {
        if (_externalTaskSource is null) return;
        var external = _externalTaskSource();
        if (external.Count == 0) return;

        // Build a quick lookup of ids already in the controller's collection.
        var existingIds = new HashSet<string>(Tasks.Select(t => t.Id), StringComparer.Ordinal);
        foreach (var task in external)
        {
            if (!existingIds.Contains(task.Id))
            {
                Tasks.Add(task);
                existingIds.Add(task.Id);
            }
        }
    }

    /// <summary>
    /// Returns tasks from <see cref="Tasks"/> that have not yet been queued in
    /// this drain and do not need review. Used to discover tasks that were added
    /// to the controller's collection after the drain started.
    /// </summary>
    private static List<RelayTaskItem> CollectNewTasks(
        IList<RelayTaskItem> tasks,
        HashSet<string> seenIds)
    {
        return tasks.Where(t => !t.NeedsReview && !seenIds.Contains(t.Id)).ToList();
    }

    /// <summary>
    /// Merges newly-discovered tasks into the current execution queue, preserving
    /// the existing queue's relative order while inserting new tasks at their
    /// position in <paramref name="tasks"/> (honouring any user reorder). New
    /// tasks never jump ahead of the task currently being processed.
    /// </summary>
    private static List<RelayTaskItem> MergeNewTasksIntoQueue(
        List<RelayTaskItem> currentQueue,
        List<RelayTaskItem> newTasks,
        IList<RelayTaskItem> tasks)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tasks.Count; i++)
            rank[tasks[i].Id] = i;

        var newSorted = newTasks.OrderBy(t => rank.GetValueOrDefault(t.Id, int.MaxValue)).ToList();
        var merged = new List<RelayTaskItem>(currentQueue.Count + newSorted.Count);
        var newIdx = 0;

        foreach (var current in currentQueue)
        {
            var currentRank = rank.GetValueOrDefault(current.Id, int.MaxValue);
            while (newIdx < newSorted.Count &&
                   rank.GetValueOrDefault(newSorted[newIdx].Id, int.MaxValue) < currentRank)
            {
                merged.Add(newSorted[newIdx]);
                newIdx++;
            }
            merged.Add(current);
        }
        while (newIdx < newSorted.Count)
        {
            merged.Add(newSorted[newIdx]);
            newIdx++;
        }
        return merged;
    }
}
