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

        // Write a full status record: all 11 stages "done".
        var statusEntries = RelayStages.All
            .Select(s => new StageStatusEntry(s.Number, s.Name, "Done"))
            .ToArray();
        var taskDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        await StageStatusRecord.WriteAsync(taskDir, statusEntries);
        // Write report files for stages 1-10 (stage 11 Commit has no report).
        for (var i = 1; i <= 10; i++)
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
        await WaitHelpers.WaitUntilAsync(() => viewModel.SelectedTaskMetricLabel != "No run history");

        Assert.Equal(11, viewModel.Stages.Count);
        Assert.All(viewModel.Stages, stage => Assert.Equal("Complete", stage.StatusLabel));
        Assert.All(viewModel.Stages, stage => Assert.Equal("Done", stage.Status));
        // Commit stage (11) has no report but its status comes from the record.
        Assert.Equal("Complete", viewModel.Stages[10].StatusLabel);
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

        // Write a status record: stages 1-3 done, stage 4 flagged, stages 5-11 waiting.
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
        // Write report files for stages 1-4 (stages 5-11 were never reached).
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
        await WaitHelpers.WaitUntilAsync(() => viewModel.HasSelectedTaskError);

        Assert.Equal(11, viewModel.Stages.Count);
        // Stages 1-3: Done / Complete
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("Done", viewModel.Stages[i].Status);
            Assert.Equal("Complete", viewModel.Stages[i].StatusLabel);
        }
        // Stage 4: Flagged
        Assert.Equal("Flagged", viewModel.Stages[3].Status);
        Assert.Equal("Flagged", viewModel.Stages[3].StatusLabel);
        // Stages 5-11: Waiting
        for (var i = 4; i < 11; i++)
        {
            Assert.Equal("Waiting", viewModel.Stages[i].Status);
            Assert.Equal("Waiting", viewModel.Stages[i].StatusLabel);
        }
        // Error from the flagged entry is surfaced.
        Assert.True(viewModel.HasSelectedTaskError);
        Assert.Equal("manifest may not include task files", viewModel.SelectedTaskError);
    }
}
