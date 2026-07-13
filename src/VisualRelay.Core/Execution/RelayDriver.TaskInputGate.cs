using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Hard-fails a run when the task input is missing or empty before any stage
// executes, preventing the pipeline from fabricating a plausible task from an
// empty prompt (the proven 2026-07-12 incident). Split out of RelayDriver.cs
// to keep that file under the size guard.
public sealed partial class RelayDriver
{
    /// <summary>
    /// Returns <c>null</c> (no-op) when <paramref name="task"/> is non-null AND
    /// <paramref name="input"/>.Markdown is non-whitespace. Otherwise hard-fails
    /// the run: writes a NEEDS-REVIEW marker, publishes an error event, persists
    /// status, and returns a flagged outcome so no stage ever executes against an
    /// empty input.
    /// </summary>
    private async Task<RelayTaskOutcome?> FailIfTaskInputMissingAsync(
        RelayTaskItem? task,
        RelayTaskInput input,
        string rootPath,
        string tasksDir,
        string runId,
        string taskId,
        string taskDirectory,
        List<StageStatusEntry> statusEntries,
        CancellationToken cancellationToken)
    {
        // When task is null and the tasks directory itself is absent from disk,
        // this is a plan-phase worktree (PlanningWorktree only copies config, not
        // the tasks dir). The main repo still has the real task spec; this is not
        // the incident where a previously-present task folder was deleted mid-drain.
        if (task is null && !Directory.Exists(Path.Combine(rootPath, tasksDir)))
            return null;

        if (task is not null && !string.IsNullOrWhiteSpace(input.Markdown))
            return null;

        const string reason = "task spec missing or empty at run start — refusing to run stages against an empty input";

        try
        {
            Directory.CreateDirectory(taskDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(taskDirectory, "NEEDS-REVIEW"),
                reason + Environment.NewLine,
                cancellationToken);

            await _dependencies.EventSink.PublishAsync(
                new RelayEvent(
                    DateTimeOffset.UtcNow,
                    "error",
                    "empty_task_input",
                    runId,
                    rootPath,
                    taskId,
                    Data: new Dictionary<string, string> { ["reason"] = reason }),
                cancellationToken);

            foreach (var e in statusEntries.Where(e => e.Status == "Pending").ToList())
            {
                MarkStatusFlagged(statusEntries, e.Stage, reason);
            }
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort marker/event/status — still return the failed outcome.
        }

        return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Flagged, null, null, reason);
    }
}
