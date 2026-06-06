using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayRunHistoryTests
{
    [Fact]
    public void ReadTaskMetric_SumsCostAndTimeAcrossAttemptsAndKeepsLatestOutcome()
    {
        using var repo = TestRepository.Create();
        // attempt1 succeeded (cost from stage seconds = 1); attempt2 failed (latest outcome).
        WriteAttemptReport(repo.Root, "redo", 1, 1, """{ "answer": "ok" }""");
        WriteAttemptReport(repo.Root, "redo", 1, 2, """{ "outcome": "error", "exit_code": 1 }""");

        var stage = RelayRunHistory.ReadTaskMetric(repo.Root, "redo").Stages.Single(s => s.StageNumber == 1);

        // Latest attempt (2) errored, so the stage outcome is the latest, not the first.
        Assert.False(stage.Succeeded);
        // total_llm_time_s is the attempt index in WriteAttemptReport: 1 + 2 = 3.
        Assert.Equal(3, stage.DurationSeconds, precision: 2);
        // Each attempt has 1 llm_call → 2 turns summed.
        Assert.Equal(2, stage.Turns);
    }

    [Fact]
    public void ReadTaskMetric_OrdersAttemptsNumericallyBeyondNine()
    {
        using var repo = TestRepository.Create();
        // attempt2 succeeds; attempt10 (the genuine latest) errors. Ordinal string sort
        // would rank "attempt10" before "attempt2" and wrongly pick attempt2 as latest.
        WriteAttemptReport(repo.Root, "deep", 1, 2, """{ "answer": "ok" }""");
        WriteAttemptReport(repo.Root, "deep", 1, 10, """{ "outcome": "error", "exit_code": 1 }""");

        var stage = RelayRunHistory.ReadTaskMetric(repo.Root, "deep").Stages.Single(s => s.StageNumber == 1);

        Assert.False(stage.Succeeded);
        Assert.Equal(repo.AttemptReportPath("deep", 1, 10), stage.ReportPath);
    }

    [Fact]
    public void FindTraceFiles_SelectsOnlyLatestAttemptPerStageNotAMerge()
    {
        using var repo = TestRepository.Create();
        WriteSession(repo.Root, "merge", 1, 1, "a.jsonl");
        WriteSession(repo.Root, "merge", 1, 2, "b.jsonl");
        WriteSession(repo.Root, "merge", 2, 1, "c.jsonl");

        var files = RelayTraceLocator.FindTraceFiles(repo.Root, "merge");

        // Stage 1 -> latest attempt (2); stage 2 -> its only attempt (1). attempt1/a.jsonl excluded.
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.EndsWith("b.jsonl", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("c.jsonl", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.EndsWith("a.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public void FindTraceFiles_PicksHighestAttemptBeyondNineForRequestedStage()
    {
        using var repo = TestRepository.Create();
        WriteSession(repo.Root, "deep-trace", 1, 2, "early.jsonl");
        WriteSession(repo.Root, "deep-trace", 1, 10, "latest.jsonl");

        var files = RelayTraceLocator.FindTraceFiles(repo.Root, "deep-trace", stageNumber: 1);

        Assert.Single(files);
        Assert.EndsWith("latest.jsonl", files[0], StringComparison.Ordinal);
    }

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
    public void ReadTaskMetric_CapturesErrorMessageFromErroredReport()
    {
        using var repo = TestRepository.Create();
        WriteReport(repo.Root, "add-multiply", 1, """{ "outcome": "error", "exit_code": 1, "error_message": "the runner exploded" }""");

        var metric = RelayRunHistory.ReadTaskMetric(repo.Root, "add-multiply");

        var stage = metric.Stages.Single(item => item.StageNumber == 1);
        Assert.False(stage.Succeeded);
        Assert.Equal("the runner exploded", stage.ErrorMessage);
    }

    [Fact]
    public void ReadTaskMetric_LeavesErrorMessageNullForCleanReport()
    {
        using var repo = TestRepository.Create();
        WriteReport(repo.Root, "add-multiply", 1, """{ "answer": "framed" }""");

        var metric = RelayRunHistory.ReadTaskMetric(repo.Root, "add-multiply");

        var stage = metric.Stages.Single(item => item.StageNumber == 1);
        Assert.True(stage.Succeeded);
        Assert.Null(stage.ErrorMessage);
    }

    [Fact]
    public void ReadTaskMetric_LeavesErrorMessageNullWhenFailedResultOmitsMessage()
    {
        using var repo = TestRepository.Create();
        WriteReport(repo.Root, "add-multiply", 1, """{ "outcome": "error" }""");

        var metric = RelayRunHistory.ReadTaskMetric(repo.Root, "add-multiply");

        var stage = metric.Stages.Single(item => item.StageNumber == 1);
        Assert.False(stage.Succeeded);
        Assert.Null(stage.ErrorMessage);
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

    [Fact]
    public void ApplyMetric_ShowsTurnsWhenPresent()
    {
        var row = new StageRowViewModel(RelayStages.All[0]);

        row.ApplyMetric(MetricFor(succeeded: true, turns: 17));

        Assert.Equal("17t", row.TurnsLabel);
        Assert.Contains("17t", row.MetricLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyMetric_OmitsTurnsWhenZero()
    {
        var row = new StageRowViewModel(RelayStages.All[0]);

        row.ApplyMetric(MetricFor(succeeded: true)); // turns defaults to 0

        Assert.Equal(string.Empty, row.TurnsLabel);
        Assert.DoesNotContain("t", row.MetricLabel, StringComparison.Ordinal);
    }

    private static StageRunMetric MetricFor(bool succeeded, int turns = 0) => new(
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
        CacheWriteTokens: 0,
        Turns: turns,
        ReportPath: "report.json",
        TraceDirectory: null,
        Succeeded: succeeded);

    private static void WriteReport(string root, string taskId, int stage, string resultJson) =>
        WriteAttemptReport(root, taskId, stage, 1, resultJson);

    private static void WriteAttemptReport(string root, string taskId, int stage, int attempt, string resultJson)
    {
        var taskDirectory = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        File.WriteAllText(
            Path.Combine(taskDirectory, $"stage{stage}-attempt{attempt}.report.json"),
            $$"""
            {
              "timestamp": "2026-05-31T20:00:0{{stage}}+00:00",
              "model": "cheap-kimi",
              "result": {{resultJson}},
              "stats": { "total_llm_time_s": {{attempt}}, "prompt_cache": { "cached_tokens": 0 } },
              "timeline": [{ "type": "llm_call", "prompt_tokens_est": 1000 }]
            }
            """);
    }

    private static void WriteSession(string root, string taskId, int stage, int attempt, string sessionFile)
    {
        var attemptDirectory = Path.Combine(root, ".relay", taskId, $"stage{stage}-attempt{attempt}");
        Directory.CreateDirectory(attemptDirectory);
        File.WriteAllText(
            Path.Combine(attemptDirectory, sessionFile),
            """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""");
    }
}
