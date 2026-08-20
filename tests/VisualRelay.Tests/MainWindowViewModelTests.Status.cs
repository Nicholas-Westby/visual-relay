using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task LoadRunHistoryAsync_CompletedRun_AllStagesShowComplete()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var taskId = "completed-task";
        var completed = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-1");
        Directory.CreateDirectory(completed);
        await File.WriteAllTextAsync(Path.Combine(completed, $"DONE-{taskId}.md"), "# Completed task\n");
        var statusEntries = RelayStages.All
            .Select(s => new StageStatusEntry(s.Number, s.Name, "Done"))
            .ToArray();
        var taskDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        await StageStatusRecord.WriteAsync(taskDir, statusEntries);
        for (var i = 1; i <= 11; i++)
            File.WriteAllText(Path.Combine(taskDir, $"stage{i}-attempt1.report.json"),
                $$"""{"timestamp":"2026-06-07T16:00:0{{i}}+00:00","model":"cheap","result":{"answer":"stage {{i}} done"},"stats":{"total_llm_time_s":1},"timeline":[{"type":"llm_call","prompt_tokens_est":1000}]}""");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        await viewModel.ToggleArchiveCommand.ExecuteAsync(null);
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        Assert.Equal(taskId, viewModel.SelectedTask.Id);
        await viewModel.LastSelectionLoad!;
        Assert.Equal(12, viewModel.Stages.Count);
        Assert.All(viewModel.Stages, stage => Assert.Equal("Done", stage.Status));
        for (var i = 0; i < 11; i++)
            Assert.Equal("Completed in 1s", viewModel.Stages[i].StatusLabel);
        Assert.Equal("Complete", viewModel.Stages[11].StatusLabel);
        Assert.False(viewModel.HasSelectedTaskError);
    }

    [Fact]
    public async Task LoadRunHistoryAsync_MidPipelineFlagged_HasCorrectPerStageStatus()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var taskId = "flagged-mid";
        var completed = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-1");
        Directory.CreateDirectory(completed);
        await File.WriteAllTextAsync(Path.Combine(completed, $"DONE-{taskId}.md"), "# Flagged mid-pipeline\n");
        var statusEntries = new List<StageStatusEntry>();
        foreach (var stage in RelayStages.All)
        {
            var status = stage.Number switch { < 4 => "Done", 4 => "Flagged", _ => "Waiting" };
            var error = stage.Number == 4 ? "manifest may not include task files" : null;
            statusEntries.Add(new StageStatusEntry(stage.Number, stage.Name, status, Error: error));
        }
        var taskDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        await StageStatusRecord.WriteAsync(taskDir, statusEntries);
        for (var i = 1; i <= 4; i++)
        {
            var outcome = i == 4 ? """{ "outcome": "error", "exit_code": 1 }""" : $$"""{ "answer": "stage {{i}} done" }""";
            File.WriteAllText(Path.Combine(taskDir, $"stage{i}-attempt1.report.json"),
                $$"""{"timestamp":"2026-06-07T16:00:0{{i}}+00:00","model":"cheap","result":{{outcome}},"stats":{"total_llm_time_s":1},"timeline":[{"type":"llm_call","prompt_tokens_est":1000}]}""");
        }
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        await viewModel.ToggleArchiveCommand.ExecuteAsync(null);
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        Assert.Equal(taskId, viewModel.SelectedTask.Id);
        await viewModel.LastSelectionLoad!;
        Assert.Equal(12, viewModel.Stages.Count);
        for (var i = 0; i < 3; i++)
        { Assert.Equal("Done", viewModel.Stages[i].Status); Assert.Equal("Completed in 1s", viewModel.Stages[i].StatusLabel); }
        Assert.Equal("Flagged", viewModel.Stages[3].Status);
        Assert.Equal("Flagged", viewModel.Stages[3].StatusLabel);
        for (var i = 4; i < 12; i++)
        { Assert.Equal("Waiting", viewModel.Stages[i].Status); Assert.Equal("Waiting", viewModel.Stages[i].StatusLabel); }
        Assert.True(viewModel.HasSelectedTaskError);
        Assert.Equal("manifest may not include task files", viewModel.SelectedTaskError);
    }

    [Fact]
    public async Task LoadRunHistoryAsync_ReviewPairRunning_BothStagesShowRunning()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var taskId = "pair-running";
        var activeDir = Path.Combine(repo.Root, "llm-tasks", "active");
        Directory.CreateDirectory(activeDir);
        await File.WriteAllTextAsync(Path.Combine(activeDir, $"{taskId}.md"), "# Pair running\n");
        var statusEntries = new List<StageStatusEntry>();
        foreach (var stage in RelayStages.All)
        {
            var status = stage.Number switch { < 7 => "Done", 7 or 8 => "Running", _ => "Waiting" };
            statusEntries.Add(new StageStatusEntry(stage.Number, stage.Name, status));
        }
        var taskDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        await StageStatusRecord.WriteAsync(taskDir, statusEntries);
        for (var i = 1; i <= 6; i++)
            File.WriteAllText(Path.Combine(taskDir, $"stage{i}-attempt1.report.json"),
                $$"""{"timestamp":"2026-06-07T16:00:0{{i}}+00:00","model":"cheap","result":{"answer":"stage {{i}} done"},"stats":{"total_llm_time_s":1},"timeline":[{"type":"llm_call","prompt_tokens_est":1000}]}""");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        // Pre-register an active run so LoadRunHistoryAsync (triggered by
        // LoadInitialAsync's initial task selection) preserves "Running" status.
        typeof(MainWindowViewModel)
            .GetField("_runningTaskIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(viewModel, new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal) { taskId });
        await viewModel.LoadInitialAsync();
        await (viewModel.LastSelectionLoad ?? Task.CompletedTask);
        viewModel.SelectedTask = viewModel.Tasks.First(t => t.Id == taskId);
        await viewModel.LastSelectionLoad!;
        Assert.Equal(12, viewModel.Stages.Count);
        for (var i = 0; i < 6; i++)
        { Assert.Equal("Done", viewModel.Stages[i].Status); Assert.Equal("Completed in 1s", viewModel.Stages[i].StatusLabel); }
        Assert.Equal("Running", viewModel.Stages[6].Status);
        Assert.Equal("Running", viewModel.Stages[7].Status);
        for (var i = 8; i < 12; i++)
            Assert.Equal("Waiting", viewModel.Stages[i].Status);
    }

    [Fact]
    public async Task LoadRunHistoryAsync_ReviewPairRunning_TaskRowLabelShowsBoth()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var taskId = "pair-label";
        var activeDir = Path.Combine(repo.Root, "llm-tasks", "active");
        Directory.CreateDirectory(activeDir);
        await File.WriteAllTextAsync(Path.Combine(activeDir, $"{taskId}.md"), "# Pair label\n");
        var statusEntries = RelayStages.All.Select(s => new StageStatusEntry(s.Number, s.Name,
            s.Number < 7 ? "Done" : s.Number is 7 or 8 ? "Running" : "Waiting")).ToList();
        var taskDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        await StageStatusRecord.WriteAsync(taskDir, statusEntries);
        for (var i = 1; i <= 6; i++)
            File.WriteAllText(Path.Combine(taskDir, $"stage{i}-attempt1.report.json"),
                $$"""{"timestamp":"2026-06-07T16:00:0{{i}}+00:00","model":"cheap","result":{"answer":"s{{i}}"},"stats":{"total_llm_time_s":1},"timeline":[{"type":"llm_call","prompt_tokens_est":1000}]}""");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        await viewModel.LastSelectionLoad!;
        viewModel.RestoreRunningTaskState(taskId, 7, "Review");
        RelayEventTestDispatch.Dispatch(viewModel,
            RelayEventTestDispatch.StageStart(taskId, 8, DateTimeOffset.UtcNow));
        Assert.Contains("07+08", viewModel.SelectedTask.RunningStepLabel, StringComparison.Ordinal);
        Assert.Contains("Review", viewModel.SelectedTask.RunningStepLabel, StringComparison.Ordinal);
        Assert.Contains("Visual-review", viewModel.SelectedTask.RunningStepLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyStageEvent_SkippedVisualReviewStageDone_SettlesRowToSkipped()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var taskId = "visual-skipped";
        var activeDir = Path.Combine(repo.Root, "llm-tasks", "active");
        Directory.CreateDirectory(activeDir);
        await File.WriteAllTextAsync(Path.Combine(activeDir, $"{taskId}.md"), "# Visual skipped\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        await viewModel.LastSelectionLoad!;
        Assert.Equal(12, viewModel.Stages.Count);
        RelayEventTestDispatch.Dispatch(viewModel,
            RelayEventTestDispatch.StageStart(taskId, 8, DateTimeOffset.UtcNow));
        Assert.Equal("Running", viewModel.Stages[7].Status);
        RelayEventTestDispatch.Dispatch(viewModel,
            RelayEventTestDispatch.StageDoneSkipped(taskId, 8, DateTimeOffset.UtcNow));
        Assert.Equal("Skipped", viewModel.Stages[7].Status);
    }

    [Fact]
    public async Task StageDoneEvent_RemovesStageFromRunningSet()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var taskId = "running-set";
        var activeDir = Path.Combine(repo.Root, "llm-tasks", "active");
        Directory.CreateDirectory(activeDir);
        await File.WriteAllTextAsync(Path.Combine(activeDir, $"{taskId}.md"), "# Running set\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        await viewModel.LastSelectionLoad!;
        viewModel.RestoreRunningTaskState(taskId, 7, "Review");
        var row = viewModel.Tasks.First(t => t.Id == taskId);
        Assert.Contains("Review", row.RunningStepLabel, StringComparison.Ordinal);
        RelayEventTestDispatch.Dispatch(viewModel,
            RelayEventTestDispatch.StageStart(taskId, 8, DateTimeOffset.UtcNow));
        Assert.Contains("07", row.RunningStepLabel, StringComparison.Ordinal);
        Assert.Contains("08", row.RunningStepLabel, StringComparison.Ordinal);
        Assert.Contains("Review", row.RunningStepLabel, StringComparison.Ordinal);
        Assert.Contains("Visual-review", row.RunningStepLabel, StringComparison.Ordinal);
        RelayEventTestDispatch.Dispatch(viewModel,
            RelayEventTestDispatch.StageDone(taskId, 8, DateTimeOffset.UtcNow, seconds: 10));
        Assert.Contains("Stage 07 · Review", row.RunningStepLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("08", row.RunningStepLabel, StringComparison.Ordinal);
        RelayEventTestDispatch.Dispatch(viewModel,
            RelayEventTestDispatch.StageDone(taskId, 7, DateTimeOffset.UtcNow, seconds: 10));
        Assert.Equal("Running task", row.RunningStepLabel);
    }

    [Fact]
    public async Task ConcurrentPairLabel_CompactFormat()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var taskId = "pair-format";
        var activeDir = Path.Combine(repo.Root, "llm-tasks", "active");
        Directory.CreateDirectory(activeDir);
        await File.WriteAllTextAsync(Path.Combine(activeDir, $"{taskId}.md"), "# Pair format\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        await viewModel.LastSelectionLoad!;
        viewModel.RestoreRunningTaskState(taskId, 7, "Review");
        RelayEventTestDispatch.Dispatch(viewModel,
            RelayEventTestDispatch.StageStart(taskId, 8, DateTimeOffset.UtcNow));
        var row = viewModel.Tasks.First(t => t.Id == taskId);
        var label = row.RunningStepLabel;
        Assert.DoesNotContain(" & ", label, StringComparison.Ordinal);
        Assert.Contains("+", label, StringComparison.Ordinal);
        Assert.Contains("Review", label, StringComparison.Ordinal);
        Assert.Contains("Visual-review", label, StringComparison.Ordinal);
        Assert.Contains(" ∥ ", label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveProgressFraction_IncrementsOnStageDone()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var taskId = "live-progress";
        var activeDir = Path.Combine(repo.Root, "llm-tasks", "active");
        Directory.CreateDirectory(activeDir);
        await File.WriteAllTextAsync(Path.Combine(activeDir, $"{taskId}.md"), "# Live progress\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        await viewModel.LastSelectionLoad!;
        viewModel.RestoreRunningTaskState(taskId, 1, "Ideate");
        var row = viewModel.Tasks.First(t => t.Id == taskId);
        var denominator = (double)RelayStages.All.Count;
        Assert.Equal(0.0, row.ProgressFraction);
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        for (var i = 1; i <= 6; i++)
            RelayEventTestDispatch.Dispatch(viewModel,
                RelayEventTestDispatch.StageDone(taskId, i, DateTimeOffset.UtcNow, seconds: 5));
        Assert.Equal(6.0 / denominator, row.ProgressFraction, precision: 6);
        Assert.Equal(6, changed.Count(p => p == "ProgressFraction"));
    }

    [Fact]
    public async Task RestoreRunningTaskState_SeedsLiveProgressFraction()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var taskId = "restore-progress";
        var activeDir = Path.Combine(repo.Root, "llm-tasks", "active");
        Directory.CreateDirectory(activeDir);
        await File.WriteAllTextAsync(Path.Combine(activeDir, $"{taskId}.md"), "# Restore progress\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        await viewModel.LastSelectionLoad!;
        var denominator = (double)RelayStages.All.Count;

        viewModel.RestoreRunningTaskState(taskId, 3, "Diagnose");
        Assert.Equal(2.0 / denominator,
            viewModel.Tasks.First(t => t.Id == taskId).ProgressFraction, precision: 6);

        viewModel.RestoreRunningTaskState(taskId, 1, "Ideate");
        Assert.Equal(0.0, viewModel.Tasks.First(t => t.Id == taskId).ProgressFraction);

        viewModel.RestoreRunningTaskState(taskId, null, null);
        Assert.Equal(0.0, viewModel.Tasks.First(t => t.Id == taskId).ProgressFraction);
    }
}
