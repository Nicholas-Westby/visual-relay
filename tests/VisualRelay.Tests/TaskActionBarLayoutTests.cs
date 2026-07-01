using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Layout tests for the "Mark done" button in the TaskActionBar header.
/// Verifies the button is visible/hidden correctly based on task state.
/// </summary>
[Collection("Headless")]
public sealed class TaskActionBarLayoutTests
{
    [AvaloniaFact]
    public async Task MarkDoneButton_Visible_WhenNonArchivedTaskSelected()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], archiveOnDone: true);
        repo.WriteNestedTask("windows-support", "# Windows Support\n\nCross-platform fixes.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "windows-support");
        Dispatcher.UIThread.RunJobs();
        await (viewModel.LastSelectionLoad ?? Task.CompletedTask);

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 900,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Locate the "Mark done" button via the TaskActionBar name scope.
        var taskActionBar = window.GetVisualDescendants()
            .OfType<TaskActionBar>()
            .Single();

        var markDoneButton = taskActionBar.FindNameScope()?.Find("MarkDoneButton") as Button;
        Assert.True(markDoneButton is not null,
            "'Mark done' button must exist in the TaskActionBar name scope.");

        Assert.True(markDoneButton!.IsVisible,
            "'Mark done' button must be visible when a non-archived task is selected.");

        // Verify the command binding resolves correctly.
        Assert.Same(
            viewModel.MarkSelectedTaskDoneCommand,
            markDoneButton.Command);
    }

    [AvaloniaFact]
    public async Task MarkDoneButton_Hidden_WhenArchivedTaskSelected()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Place an archived task under completed/.
        var completedDir = Path.Combine(repo.Root, "llm-tasks", "completed");
        Directory.CreateDirectory(completedDir);
        File.WriteAllText(
            Path.Combine(completedDir, "DONE-archived-feature.md"),
            "# Archived Feature\n\nDone long ago.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await viewModel.LoadInitialAsync();

        // Show the archive so the completed task is visible.
        viewModel.ShowArchive = true;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(viewModel.Tasks, t => t.Id == "archived-feature");
        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "archived-feature");
        Dispatcher.UIThread.RunJobs();
        await (viewModel.LastSelectionLoad ?? Task.CompletedTask);

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 900,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var taskActionBar = window.GetVisualDescendants()
            .OfType<TaskActionBar>()
            .Single();

        var markDoneButton = taskActionBar.FindNameScope()?.Find("MarkDoneButton") as Button;
        Assert.True(markDoneButton is not null,
            "'Mark done' button must exist in the TaskActionBar name scope.");

        Assert.False(markDoneButton!.IsVisible,
            "'Mark done' button must be hidden when an archived task is selected.");
    }

    [AvaloniaFact]
    public async Task MarkDoneButton_Hidden_WhenShowArchive()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Place an archived task under completed/.
        var completedDir = Path.Combine(repo.Root, "llm-tasks", "completed");
        Directory.CreateDirectory(completedDir);
        File.WriteAllText(
            Path.Combine(completedDir, "DONE-old-item.md"),
            "# Old Item\n\nPreviously finished.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await viewModel.LoadInitialAsync();

        // Toggle archive view on: the button should hide for any task
        // because the archive view is for browsing completed history.
        viewModel.ShowArchive = true;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(viewModel.Tasks, t => t.Id == "old-item");
        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "old-item");
        Dispatcher.UIThread.RunJobs();
        await (viewModel.LastSelectionLoad ?? Task.CompletedTask);

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 900,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var taskActionBar = window.GetVisualDescendants()
            .OfType<TaskActionBar>()
            .Single();

        var markDoneButton = taskActionBar.FindNameScope()?.Find("MarkDoneButton") as Button;
        Assert.True(markDoneButton is not null,
            "'Mark done' button must exist in the TaskActionBar name scope.");

        Assert.False(markDoneButton!.IsVisible,
            "'Mark done' button must be hidden when archive view is active.");
    }
}
