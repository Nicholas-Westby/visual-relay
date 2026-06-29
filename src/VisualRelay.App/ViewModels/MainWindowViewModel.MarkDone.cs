using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Tasks;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanMarkSelectedTaskDone))]
    private async Task MarkSelectedTaskDoneAsync()
    {
        if (SelectedTask is null)
            return;

        // Confirm via the shared seam (auto-resolves when headless or driven via
        // the pre-confirmed control API; opens the modal for a human click).
        var confirmed = await ConfirmAsync(
            "Mark task done",
            $"Move \"{SelectedTask.Id}\" to the archive? It won't be run by Visual Relay.",
            "Mark done");
        if (!confirmed)
            return;

        await RunBusyAsync(async () =>
        {
            await new RelayTaskRepository(RootPath).MarkDoneAsync(SelectedTask.Task);
            await ReloadTaskListAsync();
            StatusText = FormatQueueStatus();
        });
    }

    private bool CanMarkSelectedTaskDone() =>
        SelectedTask is not null &&
        !SelectedTask.IsArchived &&
        !ShowArchive &&
        !IsBusy &&
        !_runningTaskIds.Contains(SelectedTask.Id);

    public bool IsMarkDoneButtonVisible =>
        SelectedTask is not null && !SelectedTask.IsArchived && !ShowArchive;

    // ReSharper disable once UnusedParameterInPartialMethod — value parameter is part of generated partial method signature
    partial void OnShowArchiveChanged(bool value)
    {
        MarkSelectedTaskDoneCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsMarkDoneButtonVisible));
    }

    // ReSharper disable once UnusedParameterInPartialMethod — value parameter is part of generated partial method signature
    partial void OnIsBusyChanged(bool value) =>
        MarkSelectedTaskDoneCommand.NotifyCanExecuteChanged();
}
