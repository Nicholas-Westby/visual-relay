using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;
using VisualRelay.App.Views.Controls.Buttons;

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

        var markDoneButton = taskActionBar.FindNameScope()?.Find("MarkDoneButton") as CommonButton;
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

        var markDoneButton = taskActionBar.FindNameScope()?.Find("MarkDoneButton") as CommonButton;
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

        var markDoneButton = taskActionBar.FindNameScope()?.Find("MarkDoneButton") as CommonButton;
        Assert.True(markDoneButton is not null,
            "'Mark done' button must exist in the TaskActionBar name scope.");

        Assert.False(markDoneButton!.IsVisible,
            "'Mark done' button must be hidden when archive view is active.");
    }

    [AvaloniaFact]
    public void Header_TitleRenders_AndActionBarDoesNotOverflowPanel()
    {
        // Reproduce the header overflow scoped to the panel: a 620 px window
        // leaves ~586 px for the header content box, while the five
        // always-visible bar children still want ~664 px — enough to overflow
        // a non-wrapping bar without booting the whole app or a selected task.
        var vm = new MainWindowViewModel
        {
            StatusText = "Pause armed: finishing add-multiply-helper before stopping",
            SelectedTaskMetricLabel = "12 stages  2m 18s  $0.07"
        };
        var panel = new TaskDetailPanel { DataContext = vm };
        var window = new Window { Content = panel, Width = 620, Height = 420 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // (a) The TASK title must actually paint — the Auto column gives it
        //     its natural width instead of collapsing to zero.
        var title = panel.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(tb => tb.Text == "TASK");
        Assert.True(title.Bounds.Width > 0,
            "TASK title must have non-zero width in the header's Auto column.");

        // (b) The action bar's top-right corner must stay inside the panel —
        //     i.e. it wraps instead of running off the right edge and being
        //     clipped by the panel's ClipToBounds.
        var taskActionBar = panel.GetVisualDescendants()
            .OfType<TaskActionBar>()
            .Single();
        var rightEdge = taskActionBar.TranslatePoint(
            new Point(taskActionBar.Bounds.Width, 0), panel);

        Assert.NotNull(rightEdge);
        Assert.True(
            rightEdge!.Value.X <= panel.Bounds.Width,
            $"Action bar right edge ({rightEdge.Value.X:F1} px) overflows the " +
            $"panel width ({panel.Bounds.Width:F1} px).");
    }
}
