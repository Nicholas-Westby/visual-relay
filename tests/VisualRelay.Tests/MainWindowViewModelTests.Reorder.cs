using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public void MoveTask_ReordersAndKeepsMovedRowSelected()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Tasks.Add(new TaskRowViewModel(new("a", "/tmp/llm-tasks/a.md", "/tmp/llm-tasks", false, [])));
        viewModel.Tasks.Add(new TaskRowViewModel(new("b", "/tmp/llm-tasks/b.md", "/tmp/llm-tasks", false, [])));
        viewModel.Tasks.Add(new TaskRowViewModel(new("c", "/tmp/llm-tasks/c.md", "/tmp/llm-tasks", false, [])));
        var moved = viewModel.Tasks[0];
        viewModel.SelectedTask = moved;

        // Drag "a" from the top to the bottom.
        viewModel.MoveTask(0, 2);

        Assert.Equal(["b", "c", "a"], viewModel.Tasks.Select(t => t.Id));
        Assert.Same(moved, viewModel.SelectedTask);

        // Drag it back up to the middle.
        viewModel.MoveTask(2, 1);

        Assert.Equal(["b", "a", "c"], viewModel.Tasks.Select(t => t.Id));
        Assert.Same(moved, viewModel.SelectedTask);
    }

    [Fact]
    public void MoveTask_IsNoOpWhenShowingArchive()
    {
        var viewModel = new MainWindowViewModel { ShowArchive = true };
        viewModel.Tasks.Add(new TaskRowViewModel(new("a", "/tmp/llm-tasks/a.md", "/tmp/llm-tasks", false, [])));
        viewModel.Tasks.Add(new TaskRowViewModel(new("b", "/tmp/llm-tasks/b.md", "/tmp/llm-tasks", false, [])));

        viewModel.MoveTask(0, 1);

        Assert.Equal(["a", "b"], viewModel.Tasks.Select(t => t.Id));
    }

    [Fact]
    public void MoveTask_IsNoOpWhenBusy()
    {
        var viewModel = new MainWindowViewModel { IsBusy = true };
        viewModel.Tasks.Add(new TaskRowViewModel(new("a", "/tmp/llm-tasks/a.md", "/tmp/llm-tasks", false, [])));
        viewModel.Tasks.Add(new TaskRowViewModel(new("b", "/tmp/llm-tasks/b.md", "/tmp/llm-tasks", false, [])));

        viewModel.MoveTask(0, 1);

        Assert.Equal(["a", "b"], viewModel.Tasks.Select(t => t.Id));
    }

    [Fact]
    public void MoveTask_IsNoOpWhenIndicesAreOutOfRange()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Tasks.Add(new TaskRowViewModel(new("a", "/tmp/llm-tasks/a.md", "/tmp/llm-tasks", false, [])));
        viewModel.Tasks.Add(new TaskRowViewModel(new("b", "/tmp/llm-tasks/b.md", "/tmp/llm-tasks", false, [])));

        viewModel.MoveTask(-1, 1);
        viewModel.MoveTask(0, 5);
        viewModel.MoveTask(0, 0);

        Assert.Equal(["a", "b"], viewModel.Tasks.Select(t => t.Id));
    }
}
