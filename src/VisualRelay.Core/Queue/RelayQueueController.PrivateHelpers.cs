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

    private async Task WriteNeedsReviewMarkerAsync(string taskId, string reason)
    {
        var dir = Path.Combine(RootPath, ".relay", taskId);
        // If FlagAsync already wrote the marker, skip — its format is richer
        // (includes stage line).  Only write when the driver didn't set one.
        if (File.Exists(Path.Combine(dir, "NEEDS-REVIEW")))
            return;
        await RelayDriver.WriteNeedsReviewMarkerAsync(dir, reason, 0, CancellationToken.None);
    }

    private async Task ResetAndLogAsync(string taskId, string? tasksDir, string drainRunId, string phase, CancellationToken ct)
    {
        // Fix 5: When stage 12 is Flagged, the commit already landed — a checkout
        // reset would restore the sealed (Done) status.json, wiping the flag
        // evidence.  Skip the reset and log a summary entry instead.
        var statusDir = Path.Combine(RootPath, ".relay", taskId);
        var status = StageStatusRecord.Read(statusDir);
        if (status.Count >= 12 && status[11].Status == "Flagged")
        {
            DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase,
                "reset-skipped-commit-flagged", "commit sealed; skipping worktree reset to preserve flag evidence");
            return;
        }

        var gi = _gitInvoker ?? new GitInvoker();
        try
        {
            var result = await WorktreeResetter.ResetAsync(RootPath, taskId, tasksDir, gi, ct);

            if (result.SnapshotMissing)
            {
                DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase,
                    "reset-refused", "pre-run-untracked.txt is missing; refusing to delete anything on an unknown baseline");
            }
            else
            {
                if (result.Removed.Count > 0)
                {
                    var sample = string.Join(", ", result.Removed.Take(5));
                    DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase,
                        "reset-removed", $"{result.Removed.Count} untracked file(s): {sample}{(result.Removed.Count > 5 ? ", …" : "")}");
                }
                if (result.Failed.Count > 0)
                {
                    var sample = string.Join(", ", result.Failed.Take(5));
                    DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase,
                        "reset-remove-failed", $"{result.Failed.Count} untracked file(s) could not be deleted (not on disk): {sample}{(result.Failed.Count > 5 ? ", …" : "")}");
                }
            }
        }
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
    /// Builds the initial drain queue from the task list. In RestartBetweenTasks
    /// mode, needs-review tasks are excluded to prevent unbounded re-attempt
    /// loops; a skip event is written to the drain log. Standard and Sequential
    /// modes keep the 0dc9408 re-attempt behavior unchanged.
    /// </summary>
    private static List<RelayTaskItem> BuildDrainQueue(
        IList<RelayTaskItem> tasks,
        RunAllMode mode,
        string rootPath,
        string drainRunId)
    {
        var all = tasks.ToList();
        if (mode != RunAllMode.RestartBetweenTasks)
            return all;

        var flagged = all.Where(t => t.NeedsReview).ToList();
        if (flagged.Count == 0)
            return all;

        var ids = string.Join(", ", flagged.Select(t => t.Id));
        DrainSummaryLog.Write(rootPath, drainRunId, "*", "drain",
            "skipped-needs-review", $"n={flagged.Count} ids={ids}");
        return all.Where(t => !t.NeedsReview).ToList();
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

    /// <summary>Evicts a task from the drain's seen set so it becomes eligible at the next boundary. No-op when no drain is active.</summary>
    public void RemoveFromSeen(string taskId) => _drainSeenIds?.Remove(taskId);
}
