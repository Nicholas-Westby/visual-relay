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

    /// <summary>
    /// Records that a planning flag left the main checkout as it was. Planning ran in its own
    /// worktree and copied back only the task's .relay folder, so the checkout holds nothing of
    /// the task to reset, and a reset would discard the user's uncommitted work.
    /// </summary>
    private void LogPlanningFlagLeftCheckout(string taskId, string drainRunId) =>
        DrainSummaryLog.Write(RootPath, drainRunId, taskId, "plan",
            "reset-skipped-planning", "planning ran in its own worktree; the main checkout is left as it was");

    private async Task ResetAndLogAsync(string taskId, string? tasksDir, string drainRunId, string phase, bool workUncaptured, CancellationToken ct)
    {
        // The driver could not capture this flag's work, so the tree holds the only
        // copy and a reset would erase it. The circuit breaker stops the drain next.
        if (workUncaptured)
        {
            DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase,
                "reset-skipped-work-uncaptured", "flagged work was not captured; skipping worktree reset so the tree keeps it");
            return;
        }

        var gi = _gitInvoker ?? new GitInvoker();

        // A stage-12 flag used to skip the reset on the ASSUMPTION that the commit had
        // landed. During one validation it flagged precisely because staging failed, so
        // the assumption was false and the drain started the next task on a staged tree.
        // The skip now asks whether HEAD actually moved off the run base.
        var statusDir = Path.Combine(RootPath, ".relay", taskId);
        var status = StageStatusRecord.Read(statusDir);
        if (status.Count >= 12 && status[11].Status == "Flagged")
        {
            if (await CommitLandedAsync(statusDir, gi, ct))
            {
                DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase,
                    "reset-skipped-commit-flagged", "stage 12 flagged; skipping worktree reset to preserve flag evidence");
                return;
            }

            DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase,
                "reset-after-commit-flag", "stage 12 flagged with HEAD still at the run base; resetting like any other flag");
        }

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

    /// <summary>
    /// Whether the task's sealed commit actually landed: HEAD has moved off the commit
    /// the run started from. Unreadable state answers "landed", which keeps the old
    /// conservative behaviour of preserving the tree when nothing can be established.
    /// </summary>
    /// <param name="statusDir">The task's run directory.</param>
    /// <param name="git">The invoker to ask.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when HEAD differs from the recorded run base.</returns>
    private async Task<bool> CommitLandedAsync(string statusDir, IGitInvoker git, CancellationToken ct)
    {
        var runBasePath = Path.Combine(statusDir, "run-base.txt");
        if (!File.Exists(runBasePath))
            return true;

        var runBase = (await File.ReadAllTextAsync(runBasePath, ct)).Trim();
        if (string.IsNullOrEmpty(runBase))
            return true;

        var head = await git.RunAsync(RootPath, ["rev-parse", "HEAD"], ct);
        if (head.ExitCode != 0 || head.TimedOut)
            return true;

        return !string.Equals(head.Output.Trim(), runBase, StringComparison.Ordinal);
    }
}
