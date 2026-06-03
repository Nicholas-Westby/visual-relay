using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayRunHistoryTests
{
    [Fact]
    public void ReadTaskMetric_FlagsErroredStageAsNotSucceeded()
    {
        using var repo = TestRepository.Create();
        WriteReport(repo.Root, "add-multiply", 1, """{ "outcome": "error", "exit_code": 1, "error_message": "boom" }""");

        var metric = RelayRunHistory.ReadTaskMetric(repo.Root, "add-multiply");

        var stage = metric.Stages.Single(item => item.StageNumber == 1);
        Assert.False(stage.Succeeded);
    }

    [Fact]
    public void ReadTaskMetric_TreatsCleanReportAsSucceeded()
    {
        using var repo = TestRepository.Create();
        WriteReport(repo.Root, "add-multiply", 1, """{ "answer": "framed" }""");

        var metric = RelayRunHistory.ReadTaskMetric(repo.Root, "add-multiply");

        var stage = metric.Stages.Single(item => item.StageNumber == 1);
        Assert.True(stage.Succeeded);
    }

    [Fact]
    public void ApplyMetric_SetsFlaggedForFailedStage()
    {
        var row = new StageRowViewModel(RelayStages.All[0]);

        row.ApplyMetric(MetricFor(succeeded: false));

        Assert.Equal("Flagged", row.Status);
        Assert.Equal("Flagged", row.StatusLabel);
    }

    [Fact]
    public void ApplyMetric_SetsDoneForSucceededStage()
    {
        var row = new StageRowViewModel(RelayStages.All[0]);

        row.ApplyMetric(MetricFor(succeeded: true));

        Assert.Equal("Done", row.Status);
        Assert.Equal("Complete", row.StatusLabel);
    }

    private static StageRunMetric MetricFor(bool succeeded) => new(
        StageNumber: 1,
        StageName: "Ideate",
        Tier: "cheap",
        Model: "cheap-kimi",
        Timestamp: DateTimeOffset.UnixEpoch,
        DurationSeconds: 1,
        CostUsd: 0,
        Priced: true,
        PromptTokens: 0,
        CachedTokens: 0,
        OutputTokens: 0,
        ReportPath: "report.json",
        TraceDirectory: null,
        Succeeded: succeeded);

    private static void WriteReport(string root, string taskId, int stage, string resultJson)
    {
        var taskDirectory = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        File.WriteAllText(
            Path.Combine(taskDirectory, $"stage{stage}-attempt1.report.json"),
            $$"""
            {
              "timestamp": "2026-05-31T20:00:0{{stage}}+00:00",
              "model": "cheap-kimi",
              "result": {{resultJson}},
              "stats": { "total_llm_time_s": {{stage}}, "prompt_cache": { "cached_tokens": 0 } },
              "timeline": [{ "type": "llm_call", "prompt_tokens_est": 1000 }]
            }
            """);
    }
}
