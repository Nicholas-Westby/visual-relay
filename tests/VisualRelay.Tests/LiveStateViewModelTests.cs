using Avalonia.Media;
using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class LiveStateViewModelTests
{
    [Fact]
    public async Task MainWindow_ReconcilesRunningTaskAcrossTaskListReload()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("nested", "# Nested\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();
        viewModel.RestoreRunningTaskState("nested", 5, "Author-tests");

        await viewModel.RefreshCommand.ExecuteAsync(null);

        var row = Assert.Single(viewModel.Tasks, task => task.Id == "nested");
        Assert.True(row.IsRunning);
        Assert.Equal("Stage 05 · Author-tests", row.MetricsLine);
    }

    [Fact]
    public async Task MainWindow_ExposesFollowActionWhenSelectionDiffersFromRunningTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("nested", "# Nested\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();
        viewModel.RestoreRunningTaskState("nested", 8, "Fix");
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "alpha");

        Assert.True(viewModel.IsViewingDifferentTaskDuringRun);
        Assert.Equal("Viewing alpha · running nested", viewModel.ViewingRunContextText);
        Assert.True(viewModel.FollowRunningTaskCommand.CanExecute(null));

        await viewModel.FollowRunningTaskCommand.ExecuteAsync(null);

        Assert.Equal("nested", viewModel.SelectedTask?.Id);
        Assert.False(viewModel.IsViewingDifferentTaskDuringRun);
    }

    [Fact]
    public async Task MultiTask_BeginRunningTask_DoesNotMarkOtherRunningTasksIdle()
    {
        // Starting task B while task A is still running must leave A running.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task-a", "# A\n");
        repo.WriteTask("task-b", "# B\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        var taskA = viewModel.Tasks.Single(t => t.Id == "task-a");
        var taskB = viewModel.Tasks.Single(t => t.Id == "task-b");

        // Phase 1: planning starts for task-a.
        viewModel.GetType()
            .GetMethod("BeginRunningTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(viewModel, [taskA]);
        taskA.MarkPlanning(); // simulate OnPlanningStarted

        Assert.True(taskA.IsRunning);
        Assert.False(taskB.IsRunning);

        // Phase 1: planning also starts for task-b (concurrent).
        viewModel.GetType()
            .GetMethod("BeginRunningTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(viewModel, [taskB]);
        taskB.MarkPlanning();

        // Both must still be running — taskA must NOT have been marked idle.
        Assert.True(taskA.IsRunning);
        Assert.True(taskB.IsRunning);
    }

    [Fact]
    public async Task MultiTask_ClearRunningTask_OnlyClearsSpecificTask()
    {
        // Clearing task A must leave task B running.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task-a", "# A\n");
        repo.WriteTask("task-b", "# B\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        var taskA = viewModel.Tasks.Single(t => t.Id == "task-a");
        var taskB = viewModel.Tasks.Single(t => t.Id == "task-b");

        var beginRunning = viewModel.GetType()
            .GetMethod("BeginRunningTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var clearRunning = viewModel.GetType()
            .GetMethod("ClearRunningTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        beginRunning.Invoke(viewModel, [taskA]);
        taskA.MarkPlanning();
        beginRunning.Invoke(viewModel, [taskB]);
        taskB.MarkPlanning();

        Assert.True(taskA.IsRunning);
        Assert.True(taskB.IsRunning);

        // Phase 1 completes for task-a — only task-a should be idle now.
        clearRunning.Invoke(viewModel, ["task-a"]);

        Assert.False(taskA.IsRunning);
        Assert.True(taskB.IsRunning);
    }

    [Fact]
    public async Task MultiTask_UpdateRunningStage_UpdatesNonFollowedTaskRow()
    {
        // Stage events for a planning (non-followed) task must update its row.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task-a", "# A\n");
        repo.WriteTask("task-b", "# B\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        var taskA = viewModel.Tasks.Single(t => t.Id == "task-a");
        var taskB = viewModel.Tasks.Single(t => t.Id == "task-b");

        var beginRunning = viewModel.GetType()
            .GetMethod("BeginRunningTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var updateRunningStage = viewModel.GetType()
            .GetMethod("UpdateRunningStage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Start both tasks. task-b is the followed task (started second).
        beginRunning.Invoke(viewModel, [taskA]);
        taskA.MarkPlanning();
        beginRunning.Invoke(viewModel, [taskB]);
        taskB.MarkPlanning();

        // task-a advances to stage 2 (Research) — its row must update.
        updateRunningStage.Invoke(viewModel, ["task-a", 2, "Research"]);

        Assert.True(taskA.IsRunning);
        Assert.Equal("Stage 02 · Research", taskA.MetricsLine);
        Assert.True(taskB.IsRunning); // still running, unaffected
    }

    [Fact]
    public async Task MultiTask_RestoreRunningTaskState_ClearsPreviousRunningSet()
    {
        // RestoreRunningTaskState replaces the followed task and the running set.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task-a", "# A\n");
        repo.WriteTask("task-b", "# B\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        var taskA = viewModel.Tasks.Single(t => t.Id == "task-a");
        var taskB = viewModel.Tasks.Single(t => t.Id == "task-b");

        var beginRunning = viewModel.GetType()
            .GetMethod("BeginRunningTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        beginRunning.Invoke(viewModel, [taskA]);
        taskA.MarkPlanning();
        beginRunning.Invoke(viewModel, [taskB]);
        taskB.MarkPlanning();
        Assert.True(taskA.IsRunning);
        Assert.True(taskB.IsRunning);

        // Restore replaces the entire running state with a single task.
        viewModel.RestoreRunningTaskState("task-a", 5, "Author-tests");

        Assert.True(taskA.IsRunning);
        Assert.False(taskB.IsRunning);
        Assert.Equal("Stage 05 · Author-tests", taskA.MetricsLine);
    }

    [Fact]
    public void TaskRow_RunningStateOverridesPersistedTaskState()
    {
        var task = new RelayTaskItem("add-multiply", "/tmp/llm-tasks/add-multiply.md", "/tmp/llm-tasks", false, []);
        var row = new TaskRowViewModel(task);

        row.MarkRunning(5, "Author-tests");

        Assert.True(row.IsRunning);
        Assert.Equal("Running", row.StateLabel);
        Assert.Equal("Stage 05 · Author-tests", row.MetricsLine);
        Assert.Equal("#ff5ad47d", ColorOf(row.AccentBrush));
    }

    [Fact]
    public void StageRow_RunningStyleTakesPrecedenceOverLogFilterSelection()
    {
        var stage = new StageRowViewModel(new RelayStageDefinition(6, "Implement", "balanced", "llm", "all", "all", "system", "{}"), null)
        {
            Status = "Running",
            IsSelected = true
        };

        Assert.Equal("Running", stage.StatusLabel);
        Assert.Equal("#ff5ad47d", ColorOf(stage.BorderBrush));
    }

    [Fact]
    public void TaskRow_RunningElapsedLabel_IsEmptyAfterMarkRunning()
    {
        var task = new RelayTaskItem("task-x", "/tmp/llm-tasks/task-x.md", "/tmp/llm-tasks", false, []);
        var row = new TaskRowViewModel(task);

        row.MarkRunning(5, "Author-tests");

        Assert.True(row.IsRunning);
        Assert.Equal("", row.RunningElapsedLabel);
        Assert.Equal("Stage 05 · Author-tests", row.MetricsLine);
    }

    [Fact]
    public void TaskRow_MetricsLine_IncludesElapsedWhenLabelIsSet()
    {
        var task = new RelayTaskItem("task-x", "/tmp/llm-tasks/task-x.md", "/tmp/llm-tasks", false, []);
        var row = new TaskRowViewModel(task);

        row.MarkRunning(5, "Author-tests");
        row.RunningElapsedLabel = "1m 04s";

        Assert.Equal("Stage 05 · Author-tests · 1m 04s", row.MetricsLine);
    }

    [Fact]
    public void TaskRow_MarkIdle_ClearsRunningElapsedLabelAndRestoresMetricsLine()
    {
        var task = new RelayTaskItem("task-x", "/tmp/llm-tasks/task-x.md", "/tmp/llm-tasks", false,
            [], CompletedStageCount: 3, DurationSeconds: 120, CostUsd: 0.003);
        var row = new TaskRowViewModel(task);

        row.MarkRunning(5, "Author-tests");
        row.RunningElapsedLabel = "2m 36s";
        Assert.Equal("Stage 05 · Author-tests · 2m 36s", row.MetricsLine);

        row.MarkIdle();

        Assert.False(row.IsRunning);
        Assert.Equal("", row.RunningElapsedLabel);
        Assert.Equal(task.MetricsLine, row.MetricsLine);
    }

    [Fact]
    public void MainWindowViewModel_DoesNotContainDeadRunningTaskScalarFields()
    {
        // The scalar fields _runningTask, _runningStageNumber, and
        // _runningStageName on MainWindowViewModel are write-only dead
        // code — they are assigned in LiveState but never read anywhere.
        // The live state is tracked by the Dictionary<> equivalents
        // (_runningTaskIds, _runningStageNumbers, _runningStageNames).
        // Removing the dead scalars eliminates InspectCode findings
        // without changing any observable behavior.
        var fields = typeof(MainWindowViewModel)
            .GetFields(System.Reflection.BindingFlags.NonPublic |
                       System.Reflection.BindingFlags.Instance)
            .Select(f => f.Name)
            .ToHashSet();

        Assert.DoesNotContain("_runningTask", fields);
        Assert.DoesNotContain("_runningStageNumber", fields);
        Assert.DoesNotContain("_runningStageName", fields);
    }

    private static string ColorOf(IBrush brush)
    {
        var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        return solid.Color.ToString();
    }
}
