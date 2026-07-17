using CommunityToolkit.Mvvm.Input;
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
            $"Reset \"{SelectedTask.Id}\" back to Pending? The flagged run will be archived — it won't be lost, but it will start fresh from stage 1 next time.",
            "Reset");
        if (!confirmed)
            return;

        new RelayTaskRepository(RootPath).ResetTask(SelectedTask.Id);
        _activeDrainController?.RemoveFromSeen(SelectedTask.Id);
        await ReloadTaskListAsync();
        StatusText = FormatQueueStatus();
    }

    private bool CanResetSelectedTask() =>
        SelectedTask is not null &&
        SelectedTask.NeedsReview &&
        !ShowArchive;

    public bool IsResetButtonVisible =>
        SelectedTask is not null && SelectedTask.NeedsReview && !ShowArchive;
}
