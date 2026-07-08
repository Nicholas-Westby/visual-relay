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

        // Write a full status record: all 12 stages "done".
        var statusEntries = RelayStages.All
            .Select(s => new StageStatusEntry(s.Number, s.Name, "Done"))
            .ToArray();
        var taskDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        await StageStatusRecord.WriteAsync(taskDir, statusEntries);
        // Write report files for stages 1-11 (stage 12 Commit has no report).
        for (var i = 1; i <= 11; i++)
        {
            File.WriteAllText(
                Path.Combine(taskDir, $"stage{i}-attempt1.report.json"),
                $$"""
                {
                  "timestamp": "2026-06-07T16:00:0{{i}}+00:00",
                  "model": "cheap",
                  "result": { "answer": "stage {{i}} done" },
                  "stats": { "total_llm_time_s": 1 },
                  "timeline": [{ "type": "llm_call", "prompt_tokens_est": 1000 }]
                }
                """);
        }

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        await viewModel.ToggleArchiveCommand.ExecuteAsync(null);

        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        Assert.Equal(taskId, viewModel.SelectedTask.Id);
        await viewModel.LastSelectionLoad!;

        Assert.Equal(12, viewModel.Stages.Count);
        Assert.All(viewModel.Stages, stage => Assert.Equal("Done", stage.Status));
        // A completed stage with a recorded duration reads "Completed in 1s";
        // stages 1-11 each have a report so all carry a duration.
        for (var i = 0; i < 11; i++)
            Assert.Equal("Completed in 1s", viewModel.Stages[i].StatusLabel);
        // Commit stage (12) has no report (no duration) so it stays "Complete".
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

        // Write a status record: stages 1-3 done, stage 4 flagged, stages 5-12 waiting.
        var statusEntries = new List<StageStatusEntry>();
        foreach (var stage in RelayStages.All)
        {
            var status = stage.Number switch
            {
                < 4 => "Done",
                4 => "Flagged",
                _ => "Waiting"
            };
            var error = stage.Number == 4 ? "manifest may not include task files" : null;
            statusEntries.Add(new StageStatusEntry(stage.Number, stage.Name, status, Error: error));
        }

        var taskDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        await StageStatusRecord.WriteAsync(taskDir, statusEntries);
        // Write report files for stages 1-4 (stages 5-12 were never reached).
        for (var i = 1; i <= 4; i++)
        {
            var outcome = i == 4 ? """{ "outcome": "error", "exit_code": 1 }""" : $$"""{ "answer": "stage {{i}} done" }""";
            File.WriteAllText(
                Path.Combine(taskDir, $"stage{i}-attempt1.report.json"),
                $$"""
                {
                  "timestamp": "2026-06-07T16:00:0{{i}}+00:00",
                  "model": "cheap",
                  "result": {{outcome}},
                  "stats": { "total_llm_time_s": 1 },
                  "timeline": [{ "type": "llm_call", "prompt_tokens_est": 1000 }]
                }
                """);
        }

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        await viewModel.ToggleArchiveCommand.ExecuteAsync(null);

        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        Assert.Equal(taskId, viewModel.SelectedTask.Id);
        await viewModel.LastSelectionLoad!;

        Assert.Equal(12, viewModel.Stages.Count);
        // Stages 1-3: Done; each has a report so the status row shows the duration.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("Done", viewModel.Stages[i].Status);
            Assert.Equal("Completed in 1s", viewModel.Stages[i].StatusLabel);
        }
        // Stage 4: Flagged
        Assert.Equal("Flagged", viewModel.Stages[3].Status);
        Assert.Equal("Flagged", viewModel.Stages[3].StatusLabel);
        // Stages 5-12: Waiting
        for (var i = 4; i < 12; i++)
        {
            Assert.Equal("Waiting", viewModel.Stages[i].Status);
            Assert.Equal("Waiting", viewModel.Stages[i].StatusLabel);
        }
        // Error from the flagged entry is surfaced.
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

        // Write a status record: stages 1-6 done, stages 7 and 8 running, stages 9-12 waiting.
        var statusEntries = new List<StageStatusEntry>();
        foreach (var stage in RelayStages.All)
        {
            var status = stage.Number switch
            {
                < 7 => "Done",
                7 or 8 => "Running",
                _ => "Waiting"
            };
            statusEntries.Add(new StageStatusEntry(stage.Number, stage.Name, status));
        }

        var taskDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        await StageStatusRecord.WriteAsync(taskDir, statusEntries);
        // Write report files for stages 1-6.
        for (var i = 1; i <= 6; i++)
        {
            File.WriteAllText(
                Path.Combine(taskDir, $"stage{i}-attempt1.report.json"),
                $$"""
                {
                  "timestamp": "2026-06-07T16:00:0{{i}}+00:00",
                  "model": "cheap",
                  "result": { "answer": "stage {{i}} done" },
                  "stats": { "total_llm_time_s": 1 },
                  "timeline": [{ "type": "llm_call", "prompt_tokens_est": 1000 }]
                }
                """);
        }

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        Assert.Equal(taskId, viewModel.SelectedTask.Id);
        await viewModel.LastSelectionLoad!;

        Assert.Equal(12, viewModel.Stages.Count);
        // Stages 1-6: Done
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal("Done", viewModel.Stages[i].Status);
            Assert.Equal("Completed in 1s", viewModel.Stages[i].StatusLabel);
        }
        // Stages 7 (Review) and 8 (Visual-review): both Running
        Assert.Equal("Running", viewModel.Stages[6].Status);
        Assert.Equal("Running", viewModel.Stages[7].Status);
        // Stages 9-12: Waiting
        for (var i = 8; i < 12; i++)
        {
            Assert.Equal("Waiting", viewModel.Stages[i].Status);
        }
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

        // Simulate the live-state that the driver thread sets when both stages start.
        // RestoreRunningTaskState pre-loads stage 7; then dispatch stage_start for 8
        // through HandleRelayEvent so UpdateRunningStage adds it.
        viewModel.RestoreRunningTaskState(taskId, 7, "Review");
        RelayEventTestDispatch.Dispatch(viewModel,
            RelayEventTestDispatch.StageStart(taskId, 8, DateTimeOffset.UtcNow));

        // The task row label should mention both running stages.
        Assert.Contains("Stage 07", viewModel.SelectedTask.RunningStepLabel, StringComparison.Ordinal);
        Assert.Contains("Stage 08", viewModel.SelectedTask.RunningStepLabel, StringComparison.Ordinal);
    }
}
