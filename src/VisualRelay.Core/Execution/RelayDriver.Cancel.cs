using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// The wind-down for a run an operator cancelled. A cancel is not a crash: the run
/// stops, but everything it touched is left in a state the next run can start from.
/// </summary>
public sealed partial class RelayDriver
{
    /// <summary>The reason a cancelled run records in its marker, status and event.</summary>
    internal const string CancelledReason = "cancelled by operator";

    /// <summary>
    /// Records the cancelled run and restores the tree it was editing: the reached
    /// stage is marked in status.json, the partial work is captured for a later
    /// resume, NEEDS-REVIEW names the cancel, a <c>cancelled</c> event lands in
    /// run.log, and the worktree goes back to the run base.
    /// <para>
    /// Every step runs on <see cref="CancellationToken.None"/>. The run's own token
    /// is already cancelled, and a wind-down that honored it would tear instead of
    /// tidy — leaving no marker, no event, and a half-edited tree.
    /// </para>
    /// </summary>
    private async Task<RelayTaskOutcome> WindDownCancelledRunAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        List<StageStatusEntry> statusEntries)
    {
        var stage = FindCancelledStage(statusEntries);

        // Defence-in-depth, exactly as a flag: a failed file or git operation still
        // yields a valid outcome carrying the cancel reason. Each step is guarded on
        // its OWN, so a broken one cannot take the steps after it down with it — above
        // all the restore, which is the only thing that puts the repository back.
        async Task StepAsync(string step, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                await LogWindDownFailureAsync(rootPath, runId, taskId, stage, step, ex);
            }
        }

        if (stage > 0)
            await StepAsync("status", async () =>
            {
                foreach (var entry in statusEntries.Where(e => e.Status == "Running").ToList())
                    MarkStatus(statusEntries, entry.Stage, "Done");
                MarkStatusFlagged(statusEntries, stage, CancelledReason);
                await WriteStatusAsync(taskDirectory, statusEntries, CancellationToken.None);
            });

        // Same order a flag uses: capture the partial work before the marker, so
        // the marker is never the only record that work existed.
        await StepAsync("capture", () => FlaggedWorkStore.CaptureAsync(
            rootPath, taskId, taskDirectory, stage,
            _dependencies.GitInvoker, DateTimeOffset.UtcNow, CancellationToken.None));
        await StepAsync("marker", () => WriteNeedsReviewMarkerAsync(
            taskDirectory, CancelledReason, stage, CancellationToken.None));
        await StepAsync("event", () => _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "cancelled", runId, rootPath, taskId, stage,
            Data: new Dictionary<string, string> { ["reason"] = CancelledReason }), CancellationToken.None));
        await StepAsync("restore", () => RestoreRunBaseAsync(rootPath, taskId));

        return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Flagged, null, null, CancelledReason);
    }

    /// <summary>
    /// Names the wind-down step that failed, so an operator reading run.log can tell a
    /// missing marker from a tree that was never put back. Best-effort in turn: the
    /// event sink may be the very thing that just failed.
    /// </summary>
    private async Task LogWindDownFailureAsync(
        string rootPath, string runId, string taskId, int stage, string step, Exception failure)
    {
        try
        {
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "warn", "cancel_winddown_failed", runId, rootPath, taskId, stage,
                Data: new Dictionary<string, string>
                {
                    ["step"] = step,
                    ["error"] = failure.Message
                }), CancellationToken.None);
        }
        catch
        {
            // Nothing left to report through; the outcome still names the cancel.
        }
    }

    /// <summary>
    /// The stage a cancel belongs to: the one that was running, or — when the cancel
    /// landed between stages, or before the loop started — the first that has not
    /// settled yet. Never a settled stage: a resume restarts at the first unfinished
    /// entry, so rewriting a finished one would throw its work away. Zero when every
    /// stage is already settled, which leaves the marker without a stage line.
    /// </summary>
    private static int FindCancelledStage(IReadOnlyList<StageStatusEntry> entries) =>
        entries.FirstOrDefault(e => e.Status == "Running")?.Stage
        ?? entries.FirstOrDefault(e => !StageStatusIsComplete(e.Status))?.Stage
        ?? 0;

    /// <summary>
    /// Puts the working tree back where the run found it, so the repository is not
    /// left holding a half-finished stage's edits. The captured bundle keeps the
    /// work an operator may still want.
    /// </summary>
    private async Task RestoreRunBaseAsync(string rootPath, string taskId)
    {
        var config = await RelayConfigLoader.TryLoadAsync(rootPath, CancellationToken.None);
        await WorktreeResetter.ResetAsync(rootPath, taskId, config.Config.TasksDir,
            _dependencies.GitInvoker, CancellationToken.None);
    }
}
