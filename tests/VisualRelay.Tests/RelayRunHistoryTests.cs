using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayRunHistoryTests
{
    [Fact]
    public void ReadTaskMetric_SumsCostAndTimeAcrossAttemptsAndPicksLatest()
    {
        using var repo = TestRepository.Create();
        WriteAttemptReport(repo.Root, "redo", 1, 1, """{ "answer": "ok" }""");
        WriteAttemptReport(repo.Root, "redo", 1, 2, """{ "outcome": "error", "exit_code": 1 }""");

        var stage = RelayRunHistory.ReadTaskMetric(repo.Root, "redo").Stages.Single(s => s.StageNumber == 1);

        // Latest attempt (2) is kept.
        Assert.Equal(repo.AttemptReportPath("redo", 1, 2), stage.ReportPath);
        // total_llm_time_s is the attempt index in WriteAttemptReport: 1 + 2 = 3.
        Assert.Equal(3, stage.DurationSeconds, precision: 2);
        // Each attempt has 1 llm_call → 2 turns summed.
        Assert.Equal(2, stage.Turns);
    }

    [Fact]
    public void ReadTaskMetric_OrdersAttemptsNumericallyBeyondNine()
    {
        using var repo = TestRepository.Create();
        WriteAttemptReport(repo.Root, "deep", 1, 2, """{ "answer": "ok" }""");
        WriteAttemptReport(repo.Root, "deep", 1, 10, """{ "outcome": "error", "exit_code": 1 }""");

        var stage = RelayRunHistory.ReadTaskMetric(repo.Root, "deep").Stages.Single(s => s.StageNumber == 1);

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
    public void ApplyMetric_ShowsTurnsWhenPresent()
    {
        var row = new StageRowViewModel(RelayStages.All[0]);

        row.ApplyMetric(MetricFor(turns: 17));

        Assert.Equal("17t", row.TurnsLabel);
        Assert.Contains("17t", row.MetricLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyMetric_OmitsTurnsWhenZero()
    {
        var row = new StageRowViewModel(RelayStages.All[0]);

        row.ApplyMetric(MetricFor()); // turns defaults to 0

        Assert.Equal(string.Empty, row.TurnsLabel);
        Assert.DoesNotContain("t", row.MetricLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyMetric_SetsMetricLabelsButDoesNotSetStatus()
    {
        var row = new StageRowViewModel(RelayStages.All[0]);
        Assert.Equal("Waiting", row.Status);

        row.ApplyMetric(MetricFor());

        // Metric labels are set.
        Assert.Equal("1s", row.DurationLabel);
        Assert.Equal("$0.00", row.CostLabel);
        Assert.Equal("cheap-kimi", row.ModelLabel);
        Assert.Equal("report.json", row.ReportPath);
        // Status is NOT changed by ApplyMetric — it comes from the status record.
        Assert.Equal("Waiting", row.Status);
    }

    [Fact]
    public async Task ReadStatusRecord_ReturnsAllElevenEntries()
    {
        using var repo = TestRepository.Create();
        var taskDir = Path.Combine(repo.Root, ".relay", "full-task");
        Directory.CreateDirectory(taskDir);
        var entries = Enumerable.Range(1, 11)
            .Select(i => new StageStatusEntry(i, $"Stage {i}", "Done"))
            .ToList();
        await StageStatusRecord.WriteAsync(taskDir, entries);

        var result = RelayRunHistory.ReadStatusRecord(repo.Root, "full-task");

        Assert.Equal(11, result.Count);
        Assert.All(result, e => Assert.Equal("Done", e.Status));
    }

    [Fact]
    public void ReadStatusRecord_MissingFile_ReturnsEmpty()
    {
        using var repo = TestRepository.Create();

        var result = RelayRunHistory.ReadStatusRecord(repo.Root, "nonexistent");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ReadStatusRecord_StatusFromRecordNotFromReportOutcome()
    {
        // A report with "outcome": "success" (what swival actually emits) must not
        // cause the stage to be misclassified. The status comes from the record, not
        // from re-parsing report outcomes.
        using var repo = TestRepository.Create();
        var taskDir = Path.Combine(repo.Root, ".relay", "success-task");
        Directory.CreateDirectory(taskDir);
        // Write a report with "outcome": "success" (swival's actual output).
        File.WriteAllText(
            Path.Combine(taskDir, "stage1-attempt1.report.json"),
            """{ "timestamp": "2026-06-07T16:00:00+00:00", "model": "cheap-kimi", "result": { "outcome": "success" }, "stats": { "total_llm_time_s": 1 }, "timeline": [] }""");
        // Write a status record marking stage 1 as "done".
        var entries = new[] { new StageStatusEntry(1, "Ideate", "Done") };
        await StageStatusRecord.WriteAsync(taskDir, entries);

        // The metric from the report still reads fine (no longer checking outcome).
        var metric = RelayRunHistory.ReadTaskMetric(repo.Root, "success-task");
        var stage = metric.Stages.Single(s => s.StageNumber == 1);
        Assert.Equal("Ideate", stage.StageName);

        // The status comes from the record — "done", not "flagged".
        var statusRecord = RelayRunHistory.ReadStatusRecord(repo.Root, "success-task");
        var entry = statusRecord.Single();
        Assert.Equal("Done", entry.Status);
    }

    [Fact]
    public async Task ReadStatusRecord_FlaggedEntryHasError()
    {
        using var repo = TestRepository.Create();
        var taskDir = Path.Combine(repo.Root, ".relay", "flagged-task");
        Directory.CreateDirectory(taskDir);
        var entries = new[]
        {
            new StageStatusEntry(1, "Ideate", "Done"),
            new StageStatusEntry(2, "Research", "Done"),
            new StageStatusEntry(3, "Diagnose", "Flagged", Error: "something went wrong"),
            new StageStatusEntry(4, "Plan", "Waiting"),
            new StageStatusEntry(5, "Author-tests", "Waiting"),
        };
        await StageStatusRecord.WriteAsync(taskDir, entries);

        var result = RelayRunHistory.ReadStatusRecord(repo.Root, "flagged-task");

        Assert.Equal(5, result.Count);
        var flagged = result.Single(e => e.Status == "Flagged");
        Assert.Equal(3, flagged.Stage);
        Assert.Equal("something went wrong", flagged.Error);
        Assert.Equal("Done", result.Single(e => e.Stage == 1).Status);
        Assert.Equal("Waiting", result.Single(e => e.Stage == 4).Status);
    }

    private static StageRunMetric MetricFor(int turns = 0) => new(
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
        TraceDirectory: null);

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
