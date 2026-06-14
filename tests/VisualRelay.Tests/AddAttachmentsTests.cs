using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed class AddAttachmentsTests
{
    /// <summary>
    /// Headless UI/view-model regression test for the broken path:
    /// the Add Attachments button in the TASK detail pane is permanently
    /// disabled because [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]
    /// is missing on _selectedTask, _showArchive, and _isBusy.
    ///
    /// Selecting a task, toggling archive, and changing busy state should all
    /// cause the command to re-evaluate CanExecute, but the button never
    /// becomes clickable without those attributes.
    /// </summary>
    [AvaloniaFact]
    public async Task SelectTask_EnablesAddAttachments_ArchiveAndBusyDisableIt()
    {
        // ── Arrange: ViewModel with one task, no archive, not busy ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("existing", "# Existing\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        // ── Before load: no task selected → command disabled ──
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments should be disabled before any task is loaded.");

        await viewModel.LoadInitialAsync();
        Assert.False(viewModel.NeedsInitialization);

        // ── After load a task is auto-selected → command enabled ──
        Assert.NotNull(viewModel.SelectedTask);
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be enabled when a task is selected.");

        // ── Clear selection → disabled ──
        viewModel.SelectedTask = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments should be disabled when no task is selected.");

        // ── Re-select → enabled ──
        viewModel.SelectedTask = viewModel.Tasks[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be enabled when a task is selected.");

        // ── Switch to archive view → disabled ──
        viewModel.ShowArchive = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be disabled when viewing archive.");

        // ── Return to queue view → enabled ──
        viewModel.ShowArchive = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be re-enabled when leaving archive view.");

        // ── Set busy → disabled ──
        viewModel.IsBusy = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be disabled while busy.");

        // ── Clear busy → enabled ──
        viewModel.IsBusy = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be re-enabled when no longer busy.");
    }

    /// <summary>
    /// Verifies that CanExecuteChanged fires when SelectedTask, ShowArchive,
    /// and IsBusy change — the root cause of the dead Add Attachments button.
    /// Without [NotifyCanExecuteChangedFor] the event never fires, so the
    /// Button never re-queries CanExecute.
    /// </summary>
    [Fact]
    public async Task ChangingSelectedTask_NotifiesCanExecuteChanged()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("one", "# One\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        var changedCount = 0;
        viewModel.AddAttachmentsCommand.CanExecuteChanged += (_, _) => changedCount++;

        // ── Load — auto-selects the task.  Without
        //      [NotifyCanExecuteChangedFor] on _selectedTask, _showArchive,
        //      or _isBusy, the event should NOT fire during load. ──
        await viewModel.LoadInitialAsync();
        var afterLoad = changedCount;

        // ── Clear selection — must fire (after fix) ──
        viewModel.SelectedTask = null;
        Assert.True(changedCount > afterLoad,
            "CanExecuteChanged must fire when SelectedTask is cleared " +
            "(missing [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]).");

        // ── Select a task — must fire again ──
        var afterClear = changedCount;
        viewModel.SelectedTask = viewModel.Tasks[0];
        Assert.True(changedCount > afterClear,
            "CanExecuteChanged must fire when SelectedTask changes.");

        // ── Toggle ShowArchive — must fire ──
        var beforeArchive = changedCount;
        viewModel.ShowArchive = true;
        Assert.True(changedCount > beforeArchive,
            "CanExecuteChanged must fire when ShowArchive changes.");

        var beforeUnarchive = changedCount;
        viewModel.ShowArchive = false;
        Assert.True(changedCount > beforeUnarchive,
            "CanExecuteChanged must fire when ShowArchive returns to false.");

        // ── Toggle IsBusy — must fire ──
        var beforeBusy = changedCount;
        viewModel.IsBusy = true;
        Assert.True(changedCount > beforeBusy,
            "CanExecuteChanged must fire when IsBusy changes.");

        var beforeUnbusy = changedCount;
        viewModel.IsBusy = false;
        Assert.True(changedCount > beforeUnbusy,
            "CanExecuteChanged must fire when IsBusy returns to false.");
    }

    /// <summary>
    /// No selection, archive mode, or busy state each independently gate
    /// the command regardless of the other two preconditions.
    /// </summary>
    [Fact]
    public async Task AddAttachments_GatedBySelection_Archive_AndBusy()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Clear the auto-selected task → disabled.
        viewModel.SelectedTask = null;
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));

        // Select a task → enabled (not archive, not busy).
        viewModel.SelectedTask = viewModel.Tasks[0];
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null));

        // Archive mode → disabled even with a task selected.
        viewModel.ShowArchive = true;
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));

        // Leave archive → enabled again.
        viewModel.ShowArchive = false;
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null));

        // Busy → disabled.
        viewModel.IsBusy = true;
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));

        // Not busy → enabled again.
        viewModel.IsBusy = false;
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null));

        // Clear selection → disabled even though not archive and not busy.
        viewModel.SelectedTask = null;
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));
    }

    /// <summary>
    /// Executing AddAttachmentsAsync in a headless context (NullFilePicker
    /// returns an empty list) safely early-returns without side effects.
    /// This guards against regressions where the command body might throw
    /// when the picker is absent.
    /// </summary>
    [Fact]
    public async Task AddAttachmentsAsync_Headless_NoOps_WithoutCrash()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task", "# Task\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        // Sanity: before load, no task is selected → disabled.
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));

        await viewModel.LoadInitialAsync();

        // Ensure a task is selected so the command can execute.
        viewModel.SelectedTask = viewModel.Tasks[0];
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachmentsCommand must be executable after selecting a task.");

        // Execute — NullFilePicker returns empty, so this is a no-op.
        await viewModel.AddAttachmentsCommand.ExecuteAsync(null);

        // Task should still be selected and unchanged.
        Assert.NotNull(viewModel.SelectedTask);
        Assert.Equal("task", viewModel.SelectedTask.Id);
    }
}
