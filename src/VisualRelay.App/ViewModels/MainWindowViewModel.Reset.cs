using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanResetSelectedTask))]
    private async Task ResetSelectedTaskAsync()
    {
        if (SelectedTask is null)
            return;

        var confirmed = await ConfirmAsync(
            "Reset task",
            $"Reset \"{SelectedTask.Id}\" back to Pending? The flagged run will be archived — it won't be lost, but it will start fresh from stage 1 next time. "
            + "The working tree goes back to the run base; the run's edits are kept in the archived bundle.",
            "Reset");
        if (!confirmed)
            return;

        var taskId = SelectedTask.Id;
        // While a run is active the RUNNING task owns the tree, so touching it would
        // discard work that is still being produced. Archiving only is the honest
        // answer until the run ends, and the status says which of the two happened.
        var outcome = IsBusy
            ? null
            : await FlaggedTaskReset.ResetAsync(
                RootPath, taskId, RelayConfigLoader.ReadTasksDir(RootPath), new GitInvoker(), CancellationToken.None);
        if (outcome is null)
        {
            new RelayTaskRepository(RootPath).ResetTask(taskId);
        }

        _activeDrainController?.RemoveFromSeen(taskId);
        await ReloadTaskListAsync();
        StatusText = DescribeReset(taskId, outcome);
    }

    /// <summary>What the reset did, in the terms the operator has to act on.</summary>
    /// <param name="taskId">The task that was reset.</param>
    /// <param name="outcome">The tree work's result, or null when only the archive ran.</param>
    /// <returns>The status line.</returns>
    internal static string DescribeReset(string taskId, FlaggedTaskResetResult? outcome) =>
        outcome switch
        {
            null => $"Reset {taskId}; the working tree was left as is because a run is active",
            { Failure: { } failure } => $"Reset {taskId}; tree reset failed: {failure}",
            { SnapshotMissing: true } => $"Reset {taskId}; working tree restored to the run base (untracked files kept: no pre-run snapshot)",
            _ => $"Reset {taskId}; working tree restored to the run base (removed {outcome.Removed.Count} untracked files)",
        };

    private bool CanResetSelectedTask() => ResetSelectedBlockers().Count == 0;

    public bool IsResetButtonVisible =>
        SelectedTask is not null && SelectedTask.NeedsReview && !ShowArchive;
}
