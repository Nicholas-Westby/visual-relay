using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // ── Rewrite with AI ────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRewriteSelected))]
    private async Task RewriteSelectedTaskAsync()
    {
        if (SelectedTask is null)
            return;

        var id = SelectedTask.Id;

        // Confirm unless headless (ShowConfirmationAsync is null).
        if (ShowConfirmationAsync is not null)
        {
            var confirmed = await ShowConfirmationAsync(
                "Rewrite with AI",
                "Replace this task's spec with an AI-researched rewrite? The current text is kept so you can revert.");
            if (!confirmed)
                return;
        }

        // Capture undo text before starting.
        _rewriteUndo[id] = SelectedTaskMarkdown;
        _rewritingTaskIds.Add(id);
        _rewriteStartedAt[id] = DateTimeOffset.UtcNow;

        var cts = new CancellationTokenSource();
        _rewriteCts[id] = cts;
        var ct = cts.Token;

        RaiseRewriteStateChanged();

        var config = await RelayConfigLoader.LoadAsync(RootPath, ct);
        var eventSink = new ObservableRelayEventSink(HandleRelayEvent);
        var runner = new SwivalSubagentRunner(config, eventSink: eventSink);

        var task = SelectedTask.Task;

        // Run the rewrite on a background task — do NOT block the UI thread.
        _ = Task.Run(async () =>
        {
            RewriteOutcome outcome;
            try
            {
                outcome = await TaskRewriteRunner.RunAsync(RootPath, task, config, runner, ct);
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
                else if (!_rewriteUndo.ContainsKey(id))
                {
                    // Already dropped (e.g. a run started while rewriting).
                    StatusText = $"Rewrite of {id} completed but undo was discarded";
                }
                else if (outcome.Error is not null)
                {
                    _rewriteUndo.Remove(id);
                    StatusText = $"Rewrite of {id} failed: {outcome.Error}";
                }
                else
                {
                    // Unchanged — drop undo, nothing to revert.
                    _rewriteUndo.Remove(id);
                    StatusText = $"Rewrite of {id} produced no changes";
                }

                // Always reload so the new text (or unchanged text) shows.
                await ReloadTaskListAsync(SelectedTask?.Id);

                RaiseRewriteStateChanged();
            });
        }, ct);
    }

    private bool CanRewriteSelected()
    {
        if (SelectedTask is null)
            return false;

        if (SelectedTask.IsArchived)
            return false;

        if (SelectedTask.Task.CompletedStageCount != 0)
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
        if (!_rewriteUndo.TryGetValue(id, out var original))
            return;

        await RelayTaskWriter.SaveAsync(SelectedTask.Task, original);
        _rewriteUndo.Remove(id);
        await LoadSelectedTaskAsync(SelectedTask);

        StatusText = $"Reverted {id} to pre-rewrite spec";
        RaiseRewriteStateChanged();
    }

    private bool CanRevertSelected()
    {
        if (SelectedTask is null)
            return false;

        var id = SelectedTask.Id;
        return _rewriteUndo.ContainsKey(id) && !_rewritingTaskIds.Contains(id);
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
        SelectedTask is not null && _rewriteUndo.ContainsKey(SelectedTask.Id);

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
    }
}
