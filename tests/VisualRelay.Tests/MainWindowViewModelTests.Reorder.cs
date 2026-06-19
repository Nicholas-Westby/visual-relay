using VisualRelay.App.ViewModels;
using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task MoveTask_PersistsOrder_SurvivesReloadAndIsNotAlphabetical()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Initially alphabetical: alpha, beta, gamma. Drag gamma to the top.
        var gammaIndex = IndexOfTask(viewModel, "gamma");
        viewModel.MoveTask(gammaIndex, 0);
        Assert.Equal(["gamma", "alpha", "beta"], viewModel.Tasks.Select(t => t.Id));

        // Refresh reloads from disk — the manual order must NOT reset to alphabetical.
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["gamma", "alpha", "beta"], viewModel.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task ManualOrder_PersistsAcrossRestart_LoadedPurelyFromDisk()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");

        var first = new MainWindowViewModel { RootPath = repo.Root };
        await first.LoadInitialAsync();
        first.MoveTask(IndexOfTask(first, "gamma"), 0);
        first.MoveTask(IndexOfTask(first, "beta"), 1);
        Assert.Equal(["gamma", "beta", "alpha"], first.Tasks.Select(t => t.Id));

        // Simulate an app restart: a brand-new view model on the same root reads
        // the order purely from the persisted .relay/task-order.json.
        var restarted = new MainWindowViewModel { RootPath = repo.Root };
        await restarted.LoadInitialAsync();

        Assert.Equal(["gamma", "beta", "alpha"], restarted.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task NewlyCreatedTask_LandsAfterRankedTasks_WithoutDisturbingManualRanks()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Establish a manual order, then add a brand-new task on disk.
        viewModel.MoveTask(IndexOfTask(viewModel, "gamma"), 0);
        viewModel.MoveTask(IndexOfTask(viewModel, "beta"), 1);
        repo.WriteTask("delta", "# Delta\n");

        await viewModel.RefreshCommand.ExecuteAsync(null);

        // Ranked tasks keep their manual order; the unranked newcomer lands after them.
        Assert.Equal(["gamma", "beta", "alpha", "delta"], viewModel.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task ManualOrder_SurvivesDrainRefresh()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        viewModel.MoveTask(IndexOfTask(viewModel, "gamma"), 0);

        // A drain ends with RefreshTasksAfterDrainAsync; reusing the same reload path
        // it calls (RefreshCommand → ReloadTaskListAsync) must keep the manual order.
        // The persisted file is the single source of truth.
        var persisted = new TaskOrderStore(repo.Root).Read();
        Assert.Equal(["gamma", "alpha", "beta"], persisted);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(["gamma", "alpha", "beta"], viewModel.Tasks.Select(t => t.Id));
    }

    private static int IndexOfTask(MainWindowViewModel viewModel, string id)
    {
        for (var i = 0; i < viewModel.Tasks.Count; i++)
        {
            if (viewModel.Tasks[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void MoveTask_ReordersAndKeepsMovedRowSelected()
    {
        using var repo = TestRepository.Create();
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        viewModel.Tasks.Add(new TaskRowViewModel(new("a", "/tmp/llm-tasks/a.md", "/tmp/llm-tasks", false, [])));
        viewModel.Tasks.Add(new TaskRowViewModel(new("b", "/tmp/llm-tasks/b.md", "/tmp/llm-tasks", false, [])));
        viewModel.Tasks.Add(new TaskRowViewModel(new("c", "/tmp/llm-tasks/c.md", "/tmp/llm-tasks", false, [])));
        var moved = viewModel.Tasks[0];
        viewModel.SelectedTask = moved;

        // Drag "a" from the top to the bottom.
        viewModel.MoveTask(0, 2);

        Assert.Equal(["b", "c", "a"], viewModel.Tasks.Select(t => t.Id));
        Assert.Same(moved, viewModel.SelectedTask);
        // The reorder seam persists the new order on every successful move.
        Assert.Equal(["b", "c", "a"], new TaskOrderStore(repo.Root).Read());

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
