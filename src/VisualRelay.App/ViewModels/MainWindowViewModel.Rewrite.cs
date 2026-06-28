using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // ── Rewrite with AI ────────────────────────────────────────────────────

    /// <summary>
    /// Test seam: builds the sandboxed runner used by "Rewrite with AI".
    /// Production builds a real <see cref="SwivalSubagentRunner"/>; tests inject a
    /// fake so the rewrite path can be exercised without a live nono/swival.
    /// </summary>
    internal Func<RelayConfig, ISubagentRunner>? RewriteRunnerFactory { get; set; }

    [RelayCommand(CanExecute = nameof(CanRewriteSelected))]
    private async Task RewriteSelectedTaskAsync()
    {
        if (SelectedTask is null)
            return;

        var id = SelectedTask.Id;
        var task = SelectedTask.Task;

        // Confirm unless headless (ShowConfirmationAsync is null).
        if (ShowConfirmationAsync is not null)
        {
            var confirmed = await ShowConfirmationAsync(
                "Rewrite with AI",
                "Replace this task's spec with an AI-researched rewrite? The current text is kept so you can revert.",
                "Rewrite and Replace");
            if (!confirmed)
                return;
        }

        // Snapshot the WHOLE task folder before starting so a revert restores
        // attachments the rewrite may add/modify/delete — not just the spec.
        _rewriteUndo.Capture(id, task.TaskDirectory);
        _rewritingTaskIds.Add(id);
        _rewriteStartedAt[id] = DateTimeOffset.UtcNow;

        var cts = new CancellationTokenSource();
        _rewriteCts[id] = cts;
        var ct = cts.Token;

        RaiseRewriteStateChanged();

        var config = await RelayConfigLoader.LoadAsync(RootPath, ct);
        var runner = RewriteRunnerFactory?.Invoke(config)
            ?? new SwivalSubagentRunner(config, eventSink: new ObservableRelayEventSink(HandleRelayEvent), verboseDiagnostics: VerboseSandboxDiagnostics);

        // Run the rewrite on a background task — do NOT block the UI thread.
        var rewriteTask = Task.Run(async () =>
        {
            RewriteOutcome outcome;
            try
            {
                outcome = await TaskRewriteRunner.RunAsync(
                    RootPath, task, config, runner, ct, environment: EnvironmentAccessor);
            }
            catch (Exception ex)
            {
                outcome = new RewriteOutcome(false, ex.Message);
            }

            // Marshal UI updates back to the UI thread.
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                _rewritingTaskIds.Remove(id);
                _rewriteStartedAt.Remove(id);
                _rewriteCts.Remove(id);

                if (outcome.Changed)
                {
                    StatusText = $"Rewrote {id} — review and Revert if needed";
                }
                else if (!_rewriteUndo.Has(id))
                {
                    // Already dropped (e.g. a run started while rewriting).
                    StatusText = $"Rewrite of {id} completed but undo was discarded";
                }
                else if (outcome.Error is not null)
                {
                    _rewriteUndo.Discard(id);
                    StatusText = $"Rewrite of {id} failed: {outcome.Error}";
                }
                else
                {
                    // Unchanged — drop undo, nothing to revert.
                    _rewriteUndo.Discard(id);
                    StatusText = $"Rewrite of {id} produced no changes";
                }

                // Always reload so the new text (or unchanged text) shows. Reload
                // and re-select against the CAPTURED id of the task that was
                // rewritten — not the live selection, which the user may have
                // changed mid-rewrite (else the wrong task reloads).
                await ReloadTaskListAsync(id);

                RaiseRewriteStateChanged();
            });
        }, ct);

        // Expose the in-flight task (its dispatcher continuation included) so
        // headless tests can deterministically await rewrite completion. Evict the
        // entry once it settles so completed tasks aren't retained for the VM's
        // lifetime (the eviction runs on the UI thread to keep the dict single-thread).
        _rewriteTasksForTests[id] = rewriteTask;
        _ = rewriteTask.ContinueWith(
            _ => _rewriteTasksForTests.Remove(id),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private readonly Dictionary<string, Task> _rewriteTasksForTests = new(StringComparer.Ordinal);

    /// <summary>
    /// Test-only: deterministically waits for the in-flight rewrite of
    /// <paramref name="taskId"/> to fully finish — including its dispatcher
    /// completion continuation (status + reload). Pumps the headless dispatcher
    /// and polls the rewriting flag (set synchronously at launch, cleared by the
    /// completion handler), then awaits the captured task so any reload completes.
    /// </summary>
    internal async Task WaitForRewriteToFinishForTests(string taskId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (_rewritingTaskIds.Contains(taskId) && DateTimeOffset.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        if (_rewriteTasksForTests.TryGetValue(taskId, out var task))
            await task;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Test-only: the ids a drain would actually execute — the visible pending
    /// tasks minus any currently being rewritten (the drain's skip rule).
    /// </summary>
    internal IReadOnlyList<string> DrainableTaskIdsForTests() =>
        Tasks.Select(t => t.Id).Where(id => !_rewritingTaskIds.Contains(id)).ToList();

    /// <summary>
    /// Deletes any rewrite-undo snapshots still on disk (e.g. a successful
    /// rewrite the user never reverted). Called on app shutdown so the temp
    /// snapshots from <see cref="RewriteUndoStore"/> never leak.
    /// </summary>
    internal void DiscardPendingRewriteUndos() => _rewriteUndo.DiscardAll();

    private bool CanRewriteSelected()
    {
        if (SelectedTask is null)
            return false;

        if (SelectedTask.IsArchived)
            return false;

        if (SelectedTask.Task.CompletedStageCount != 0)
            return false;

        // A non-nested task's TaskDirectory is the SHARED llm-tasks/ root, which
        // the rewrite copy-back and the revert both delete recursively — so
        // rewriting it would wipe every sibling. Selection promotes tasks to
        // nested; only refuse here when that promotion did not take effect.
        if (!SelectedTask.Task.IsNested)
            return false;

        if (IsEditingMarkdown)
            return false;

        if (IsNewTaskDialogOpen)
            return false;

        var id = SelectedTask.Id;
        if (_runningTaskIds.Contains(id))
            return false;

        if (_rewritingTaskIds.Contains(id))
            return false;

        // Deliberately do NOT gate on IsBusy — rewrites run concurrently.
        return true;
    }

    /// <summary>
    /// Cancels the in-flight rewrite for the selected task. No-op safe
    /// if the rewrite already finished or errored.
    /// </summary>
    [RelayCommand]
    private void CancelRewriteSelected()
    {
        if (SelectedTask is null)
            return;

        var id = SelectedTask.Id;
        if (_rewriteCts.TryGetValue(id, out var cts))
        {
            cts.Cancel();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevertSelected))]
    private async Task RevertRewriteSelected()
    {
        if (SelectedTask is null)
            return;

        var id = SelectedTask.Id;
        if (!_rewriteUndo.Has(id))
            return;

        // Restore the WHOLE pre-rewrite folder (spec + attachments), not just the
        // spec string — the rewrite copy-back may have added/changed/removed files.
        await _rewriteUndo.RestoreAsync(id, SelectedTask.Task.TaskDirectory);
        await LoadSelectedTaskAsync(SelectedTask);

        StatusText = $"Reverted {id} to pre-rewrite state";
        RaiseRewriteStateChanged();
    }

    private bool CanRevertSelected()
    {
        if (SelectedTask is null)
            return false;

        var id = SelectedTask.Id;
        return _rewriteUndo.Has(id) && !_rewritingTaskIds.Contains(id);
    }

    // ── Computed bind targets for the RewriteToolbar ───────────────────────

    public bool IsSelectedTaskRewriting =>
        SelectedTask is not null && _rewritingTaskIds.Contains(SelectedTask.Id);

    public string SelectedTaskRewriteElapsed
    {
        get
        {
            if (SelectedTask is null)
                return string.Empty;

            if (!_rewriteStartedAt.TryGetValue(SelectedTask.Id, out var startedAt))
                return string.Empty;

            return ElapsedFormatter.Label(DateTimeOffset.UtcNow - startedAt);
        }
    }

    public bool SelectedTaskHasRewriteUndo =>
        SelectedTask is not null && _rewriteUndo.Has(SelectedTask.Id);

    public bool CanRewriteSelectedPublic => CanRewriteSelected();

    // ── Shared mutation helper ─────────────────────────────────────────────

    private void RaiseRewriteStateChanged()
    {
        OnPropertyChanged(nameof(IsSelectedTaskRewriting));
        OnPropertyChanged(nameof(SelectedTaskRewriteElapsed));
        OnPropertyChanged(nameof(SelectedTaskHasRewriteUndo));
        OnPropertyChanged(nameof(CanRewriteSelectedPublic));
        RewriteSelectedTaskCommand.NotifyCanExecuteChanged();
        CancelRewriteSelectedCommand.NotifyCanExecuteChanged();
        RevertRewriteSelectedCommand.NotifyCanExecuteChanged();
        // Rewrite state gates run/resume/edit CanExecute — keep them fresh.
        RunSelectedCommand.NotifyCanExecuteChanged();
        ResumeSelectedCommand.NotifyCanExecuteChanged();
        EditSelectedTaskCommand.NotifyCanExecuteChanged();
        MarkSelectedTaskDoneCommand.NotifyCanExecuteChanged();
        AddAttachmentsCommand.NotifyCanExecuteChanged();
    }
}
