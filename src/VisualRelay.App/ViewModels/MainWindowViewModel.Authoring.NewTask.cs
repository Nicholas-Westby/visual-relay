using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Tasks;

namespace VisualRelay.App.ViewModels;

/// <summary>
/// The new-task dialog's own half of authoring, split from
/// <c>MainWindowViewModel.Authoring.cs</c> to keep each file within the 300-line
/// guard.
/// </summary>
public partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanOpenNewTaskDialog))]
    private void OpenNewTaskDialog()
    {
        if (IsNewTaskDialogOpen)
        {
            IsNewTaskDialogOpen = false;
            NewTaskTitle = string.Empty;
            NewTaskBody = string.Empty;
            NewTaskError = null;
            return;
        }

        IsEditingMarkdown = false;
        NewTaskTitle = string.Empty;
        NewTaskBody = string.Empty;
        NewTaskError = null;
        PrepareNewTaskTemplates();
        SelectedTabIndex = 0;
        IsNewTaskDialogOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanCreateNewTask))]
    private async Task CreateNewTaskAsync()
    {
        var slug = RelayTaskWriter.Slugify(NewTaskTitle);
        NewTaskError = null;

        // Validate the derived slug.
        var validationError = RelayTaskWriter.ValidateSlug(slug, RootPath);
        if (validationError is not null)
        {
            NewTaskError = validationError;
            return;
        }

        try
        {
            var markdown = string.IsNullOrWhiteSpace(NewTaskBody)
                ? $"# {NewTaskTitle.Trim()}\n"
                : $"# {NewTaskTitle.Trim()}\n\n{NewTaskBody}";

            var createdPath = await RelayTaskWriter.CreateAsync(RootPath, slug, markdown);
            await WriteSelectedTemplateAttachmentsAsync(createdPath);
        }
        catch (Exception ex)
        {
            NewTaskError = ex.Message;
            return;
        }

        IsNewTaskDialogOpen = false;
        // The archive lists DONE-* only, so a reload taken from it can never hold
        // the task just written: the new row would be absent and the selection
        // would land on an unrelated archived task. Creating a task is a queue
        // action, so switch to the queue and let the one reload carry the slug.
        var switchedToQueue = ShowArchive;
        if (switchedToQueue)
        {
            ShowArchive = false;
        }

        await ReloadTaskListAsync(slug);
        // The count counts the new task (the control API read "0 pending" beside two); a run keeps its line.
        if (!IsBusy)
        {
            StatusText = switchedToQueue ? $"Created {slug}; switched to the queue" : FormatQueueStatus();
        }
    }

    private bool CanOpenNewTaskDialog() =>
        Directory.Exists(RootPath);

    private bool CanCreateNewTask() =>
        !string.IsNullOrWhiteSpace(NewTaskTitle) && Directory.Exists(RootPath);
}
