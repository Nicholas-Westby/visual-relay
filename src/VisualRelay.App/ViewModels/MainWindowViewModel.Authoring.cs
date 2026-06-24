using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // ── Edit markdown ────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanEditSelectedTask))]
    private void EditSelectedTask()
    {
        if (SelectedTask is null)
        {
            return;
        }

        IsNewTaskDialogOpen = false;

        // Split the markdown into title and body.
        var (title, body) = SplitMarkdownTitle(SelectedTaskMarkdown, SelectedTask.Id);
        EditTitleBuffer = title;
        EditBuffer = body;
        IsEditingMarkdown = true;
    }

    private bool CanEditSelectedTask()
    {
        if (SelectedTask is null)
        {
            EditBlockedReason = null;
            return false;
        }

        if (_runningTaskId is not null && string.Equals(SelectedTask.Id, _runningTaskId, StringComparison.Ordinal))
        {
            EditBlockedReason = "Cannot edit a running task.";
            return false;
        }

        if (_rewritingTaskIds.Contains(SelectedTask.Id))
        {
            EditBlockedReason = "Cannot edit a task while it's being rewritten.";
            return false;
        }

        if (SelectedTask.IsArchived)
        {
            EditBlockedReason = "Cannot edit an archived task.";
            return false;
        }

        if (IsEditingMarkdown)
        {
            EditBlockedReason = null;
            return false;
        }

        EditBlockedReason = null;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private async Task SaveEditAsync()
    {
        if (SelectedTask is null || !IsEditingMarkdown)
        {
            return;
        }

        var title = EditTitleBuffer.Trim();
        var body = EditBuffer;
        var newMarkdown = $"# {title}\n\n{body}";
        var titleChanged = !string.Equals(title, SelectedTaskName, StringComparison.Ordinal);
        var newSlug = titleChanged ? RelayTaskWriter.Slugify(title) : SelectedTask.Id;

        if (!titleChanged || string.Equals(newSlug, SelectedTask.Id, StringComparison.Ordinal))
        {
            // Slug unchanged or title not modified — save in place.
            await RelayTaskWriter.SaveAsync(SelectedTask.Task, newMarkdown);
        }
        else
        {
            // Rename the task directory and markdown file.
            var newPath = await RelayTaskWriter.RenameAsync(RootPath, SelectedTask.Task, newSlug, newMarkdown);
            var newDir = Path.GetDirectoryName(newPath)!;

            // Update the task item with new paths and slug.
            var oldId = SelectedTask.Id;
            SelectedTask.Task = SelectedTask.Task with { Id = newSlug, MarkdownPath = newPath, TaskDirectory = newDir };

            // Migrate tracking dictionaries.
            RekeyTaskId(oldId, newSlug);
        }

        _rewriteUndo.Discard(SelectedTask.Id);
        RaiseRewriteStateChanged();
        IsEditingMarkdown = false;
        EditTitleBuffer = string.Empty;

        // Reload the selected task to refresh markdown, context, and the queue list.
        await ReloadTaskListAsync(SelectedTask.Id);

        // After reload, SelectedTaskName is set by SelectTaskAsync, but set it
        // here as well so it's immediately visible.
        SelectedTaskName = title;
    }

    private bool CanSaveEdit() =>
        IsEditingMarkdown && SelectedTask is not null && !string.IsNullOrWhiteSpace(EditTitleBuffer);
    [RelayCommand]
    private void CancelEdit()
    {
        IsEditingMarkdown = false;
        EditBuffer = string.Empty;
        EditTitleBuffer = string.Empty;
    }

    // ── Attachments ───────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanAddAttachments))]
    private async Task AddAttachmentsAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var pick = await _filePicker.PickFilesAsync();

        // Distinguish a cancel (nothing chosen — stay silent) from a pick that
        // chose entries but resolved no usable local path (surface a reason so
        // the user never sees a silent "nothing happened").
        if (pick.ChosenCount == 0)
        {
            return;
        }

        var files = pick.Paths;
        if (files.Count == 0)
        {
            StatusText = "Couldn't attach: the selected item has no local file path.";
            return;
        }

        var currentTask = SelectedTask.Task;

        // If the task is flat, promote it once before the loop so every
        // subsequent file lands in the newly created nested directory.
        // Promoting inside the loop would crash on the second file because
        // currentTask is a record — it doesn't mutate — and the flat .md
        // was already deleted by the first promotion.
        if (!currentTask.IsNested)
        {
            var tasksDir = Path.GetDirectoryName(currentTask.MarkdownPath)!;
            var rootPath = Path.GetDirectoryName(tasksDir)!;
            var newMarkdownPath = await RelayTaskWriter.PromoteToNestedAsync(rootPath, currentTask);
            var newTaskDir = Path.GetDirectoryName(newMarkdownPath)!;
            currentTask = currentTask with { IsNested = true, MarkdownPath = newMarkdownPath, TaskDirectory = newTaskDir };
        }

        foreach (var file in files)
        {
            await RelayTaskWriter.AddAttachmentAsync(currentTask, file);
        }

        // Reload directly to the edited task's id (stable across the flat→nested
        // promotion above) so selection doesn't snap to the first task and the
        // new attachment is visible immediately.
        await ReloadTaskListAsync(currentTask.Id);

        // Refresh the status line so a stale prior message doesn't linger.
        // When the pick was partial (some items had no local path), surface a note;
        // otherwise show the standard queue status.
        var skipped = pick.ChosenCount - files.Count;
        StatusText = skipped > 0
            ? $"Added {files.Count} attachment(s); {skipped} item(s) had no local file path and were skipped."
            : FormatQueueStatus();
    }

    private bool CanAddAttachments() =>
        SelectedTask is not null && !ShowArchive && !IsBusy;

    [RelayCommand]
    private async Task RemoveAttachmentAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        var confirmed = await ConfirmRemoveAttachmentAsync(filePath);
        if (!confirmed)
        {
            return;
        }

        // Capture the edited task's id before the reload so selection stays put
        // instead of snapping to the alphabetically-first task.
        var editedTaskId = SelectedTask?.Id;

        RelayTaskWriter.RemoveAttachment(filePath);
        await ReloadTaskListAsync(editedTaskId);

        // Refresh the status line after a successful remove so a stale prior
        // message doesn't linger.
        StatusText = FormatQueueStatus();
    }

    /// <summary>
    /// Asks the user for confirmation before deleting an attachment.
    /// Override in tests via <see cref="ShowConfirmationAsync"/>; the default
    /// (when null) skips the prompt so headless/NullFilePicker scenarios work.
    /// </summary>
    private async Task<bool> ConfirmRemoveAttachmentAsync(string filePath)
    {
        if (ShowConfirmationAsync is null)
        {
            return true;
        }

        var fileName = Path.GetFileName(filePath);
        return await ShowConfirmationAsync("Remove Attachment", $"Delete \"{fileName}\"? This cannot be undone.", "Delete");
    }

    [RelayCommand]
    private void RevealAttachment(string? filePath)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            FileReveal.Reveal(filePath);
        }
    }

    // ── New task ──────────────────────────────────────────────────────────

    [RelayCommand]
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

            await RelayTaskWriter.CreateAsync(RootPath, slug, markdown);
        }
        catch (Exception ex)
        {
            NewTaskError = ex.Message;
            return;
        }

        IsNewTaskDialogOpen = false;
        await ReloadTaskListAsync(slug);
    }

    private bool CanCreateNewTask() =>
        !string.IsNullOrWhiteSpace(NewTaskTitle);
    /// <summary>
    /// True when the Markdown tab should show the read-only view — neither
    /// editing an existing task nor authoring a new one.
    /// </summary>
    public bool IsMarkdownReadOnly => !IsEditingMarkdown && !IsNewTaskDialogOpen;
}
