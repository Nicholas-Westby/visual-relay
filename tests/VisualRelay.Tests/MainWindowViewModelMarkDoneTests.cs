using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the "Mark done" command — retiring a task into the archive
/// without running it.
/// </summary>
[Collection("Headless")]
public sealed class MainWindowViewModelMarkDoneTests
{
    [AvaloniaFact]
    public async Task MarkSelectedTaskDone_RemovesTaskFromQueue()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], archiveOnDone: true);
        repo.WriteNestedTask("windows-support", "# Windows Support\n\nCross-platform fixes.");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root, ShowConfirmationAsync = null };
        await viewModel.LoadInitialAsync();

        var task = viewModel.Tasks.Single(t => t.Id == "windows-support");
        viewModel.SelectedTask = task;
        Dispatcher.UIThread.RunJobs();
        await (viewModel.LastSelectionLoad ?? Task.CompletedTask);

        // Command must be available for a non-archived task.
        Assert.True(viewModel.MarkSelectedTaskDoneCommand.CanExecute(null));

        // Act: mark done.
        await viewModel.MarkSelectedTaskDoneCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // The task must be gone from the queue.
        Assert.DoesNotContain(viewModel.Tasks, t => t.Id == "windows-support");
    }

    [AvaloniaFact]
    public async Task CanMarkSelectedTaskDone_FalseForArchivedTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Place an already-archived task under completed/.
        var completedDir = Path.Combine(repo.Root, "llm-tasks", "completed");
        Directory.CreateDirectory(completedDir);
        File.WriteAllText(Path.Combine(completedDir, "DONE-archived-task.md"), "# Archived\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root, ShowConfirmationAsync = null };
        await viewModel.LoadInitialAsync();

        // Show the archive so the completed task is visible, then select it.
        viewModel.ShowArchive = true;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "archived-task");
        Dispatcher.UIThread.RunJobs();
        await (viewModel.LastSelectionLoad ?? Task.CompletedTask);

        // An archived task must be rejected by the can-execute gate.
        Assert.False(viewModel.MarkSelectedTaskDoneCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task CanMarkSelectedTaskDone_FalseWhenShowArchive()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("active-task", "# Active\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root, ShowConfirmationAsync = null };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "active-task");
        Dispatcher.UIThread.RunJobs();
        await (viewModel.LastSelectionLoad ?? Task.CompletedTask);

        // Before: command is enabled for a non-archived task.
        Assert.True(viewModel.MarkSelectedTaskDoneCommand.CanExecute(null));

        // Set ShowArchive while a pending task is selected — the gate should block.
        viewModel.ShowArchive = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.MarkSelectedTaskDoneCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task CanMarkSelectedTaskDone_FalseWhenIsBusy()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("active-task", "# Active\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root, ShowConfirmationAsync = null };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "active-task");
        Dispatcher.UIThread.RunJobs();
        await (viewModel.LastSelectionLoad ?? Task.CompletedTask);

        // Before: command is enabled.
        Assert.True(viewModel.MarkSelectedTaskDoneCommand.CanExecute(null));

        // Set IsBusy — the gate should block.
        viewModel.IsBusy = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.MarkSelectedTaskDoneCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task CanMarkSelectedTaskDone_FalseWhenRunningTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("running-task", "# Running\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root, ShowConfirmationAsync = null };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "running-task");
        Dispatcher.UIThread.RunJobs();
        await (viewModel.LastSelectionLoad ?? Task.CompletedTask);

        // Before: command is enabled.
        Assert.True(viewModel.MarkSelectedTaskDoneCommand.CanExecute(null));

        // Simulate the task starting to run via the drain lifecycle hook.
        viewModel.CreateDrainLifecycleCallbacks().OnExecuteStarted?.Invoke("running-task");
        Dispatcher.UIThread.RunJobs();

        // A running task must be rejected.
        Assert.False(viewModel.MarkSelectedTaskDoneCommand.CanExecute(null));
    }

    [Fact]
    public void CanMarkSelectedTaskDone_FalseWhenNoTaskSelected()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("active-task", "# Active\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root, ShowConfirmationAsync = null };

        // No selected task → command should be disabled.
        Assert.Null(viewModel.SelectedTask);
        Assert.False(viewModel.MarkSelectedTaskDoneCommand.CanExecute(null));
    }
}
