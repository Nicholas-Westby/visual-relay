using System.Text.Json;
using VisualRelay.Core.Agent;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the report the new loop writes: that it is written on every exit path,
/// that it is written atomically, and that the existing cost estimator and run
/// history can still read it. The schema is not free to change — 1109 archived
/// reports and two readers depend on it field for field.
/// </summary>
public sealed class AgentReportWriterTests
{
    private static AgentLoopResult Result(
        AgentLoopOutcome outcome = AgentLoopOutcome.Success,
        string answer = "the answer",
        string? error = null) =>
        new(
            outcome,
            answer,
            new AgentStats
            {
                Turns = 3,
                ToolCallsTotal = 5,
                ToolCallsSucceeded = 4,
                ToolCallsFailed = 1,
                ToolCallsByName = new Dictionary<string, ToolCallCounts>(StringComparer.Ordinal)
                {
                    ["read_file"] = new(3, 0),
                    ["grep"] = new(1, 1),
                },
                Compactions = 1,
                GuardrailInterventions = 2,
                StormedCalls = 1,
                RecoveredResponses = 1,
                TruncationRepairs = 1,
                LlmCalls = 3,
                TotalLlmTimeSeconds = 9.5,
                TotalToolTimeSeconds = 0.75,
                PromptTokens = 3000,
                CompletionTokens = 400,
                ReasoningTokens = 350,
                CachedTokens = 200,
                CacheWriteTokens = 50,
            },
            error,
            ServedModel: "deepseek-v4-flash");

    private static JsonElement Parse(System.Text.Json.Nodes.JsonObject report) =>
        JsonDocument.Parse(report.ToJsonString()).RootElement.Clone();

    /// <summary>Every field the archived schema carries is present.</summary>
    [Fact]
    public void TheReport_KeepsTheArchivedSchema()
    {
        var report = Parse(AgentReportWriter.Build(
            Result(), "cheap", "the task prompt", DateTimeOffset.UtcNow));

        Assert.Equal(1, report.GetProperty("version").GetInt32());
        Assert.Equal("oneshot", report.GetProperty("mode").GetString());
        Assert.Equal("cheap", report.GetProperty("model").GetString());
        Assert.Equal("the task prompt", report.GetProperty("task").GetString());

        var stats = report.GetProperty("stats");
        foreach (var field in (string[])
                 ["turns", "tool_calls_total", "tool_calls_succeeded", "tool_calls_failed",
                  "tool_calls_by_name", "compactions", "turn_drops", "scavenged_calls",
                  "guardrail_interventions", "recovered_responses", "truncation_repairs",
                  "stormed_calls", "llm_calls", "total_llm_time_s", "total_tool_time_s",
                  "prompt_cache"])
            Assert.True(stats.TryGetProperty(field, out _), $"stats is missing '{field}'");
    }

    /// <summary>
    /// The two counters whose machinery was deliberately not ported are present
    /// and zero, so old and new reports stay comparable and any non-zero value
    /// reads immediately as a regression.
    /// </summary>
    [Fact]
    public void TheUnportedCounters_ArePresentAndZero()
    {
        var stats = Parse(AgentReportWriter.Build(
            Result(), "cheap", "t", DateTimeOffset.UtcNow)).GetProperty("stats");

        Assert.Equal(0, stats.GetProperty("turn_drops").GetInt32());
        Assert.Equal(0, stats.GetProperty("scavenged_calls").GetInt32());
    }

    /// <summary>
    /// The outcome and the error are promoted to fields the driver reads. Ten
    /// archived failures carried an exact cause the driver never saw, because
    /// all it received was an exit code.
    /// </summary>
    [Fact]
    public void TheOutcomeAndCause_ArePromotedToFields()
    {
        var report = Parse(AgentReportWriter.Build(
            Result(AgentLoopOutcome.Error, string.Empty, "provider returned 401"),
            "cheap", "t", DateTimeOffset.UtcNow));

        var result = report.GetProperty("result");
        Assert.Equal("error", result.GetProperty("outcome").GetString());
        Assert.Equal(1, result.GetProperty("exit_code").GetInt32());
        Assert.Equal("provider returned 401", result.GetProperty("error_message").GetString());
    }

    /// <summary>Exhaustion keeps the exit code the corpus records for it.</summary>
    [Fact]
    public void Exhaustion_KeepsItsExitCode()
    {
        var report = Parse(AgentReportWriter.Build(
            Result(AgentLoopOutcome.Exhausted, string.Empty, "turn budget exhausted"),
            "balanced", "t", DateTimeOffset.UtcNow));

        Assert.Equal("exhausted", report.GetProperty("result").GetProperty("outcome").GetString());
        Assert.Equal(2, report.GetProperty("result").GetProperty("exit_code").GetInt32());
    }

    /// <summary>
    /// A cancelled stage still produces a report. The old runner wrote none at
    /// all on SIGTERM, which is why the corpus has no interrupted outcomes.
    /// </summary>
    [Fact]
    public void ACancelledStage_StillProducesAReport()
    {
        var report = Parse(AgentReportWriter.Build(
            Result(AgentLoopOutcome.Cancelled, string.Empty, "stage cancelled"),
            "cheap", "t", DateTimeOffset.UtcNow));

        Assert.Equal("interrupted", report.GetProperty("result").GetProperty("outcome").GetString());
        Assert.Equal(3, report.GetProperty("stats").GetProperty("turns").GetInt32());
    }

    /// <summary>
    /// The concrete model that served the call is recorded beside the tier alias,
    /// so a fallback hop is no longer invisible to costing.
    /// </summary>
    [Fact]
    public void TheServedModel_IsRecordedBesideTheTierAlias()
    {
        var report = Parse(AgentReportWriter.Build(
            Result(), "cheap", "t", DateTimeOffset.UtcNow));

        Assert.Equal("cheap", report.GetProperty("model").GetString());
        Assert.Equal("deepseek-v4-flash", report.GetProperty("served_model").GetString());
    }

    /// <summary>Measured usage is carried alongside the legacy fields, not instead of them.</summary>
    [Fact]
    public void MeasuredUsage_IsCarriedAlongsideTheLegacyFields()
    {
        var stats = Parse(AgentReportWriter.Build(
            Result(), "cheap", "t", DateTimeOffset.UtcNow)).GetProperty("stats");

        var measured = stats.GetProperty("measured_usage");
        Assert.Equal(3000, measured.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(400, measured.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(350, measured.GetProperty("reasoning_tokens").GetInt32());

        // The legacy cache fields the estimator reads are still where it looks.
        Assert.Equal(200, stats.GetProperty("prompt_cache").GetProperty("cached_tokens").GetInt32());
        Assert.Equal(50, stats.GetProperty("prompt_cache").GetProperty("cache_write_tokens").GetInt32());
    }

    /// <summary>
    /// The round trip that matters: the existing estimator reads the new report
    /// and produces a priced result, so the cost panel and run history keep
    /// working through the transition.
    /// </summary>
    [Fact]
    public void TheExistingEstimator_CanStillReadTheNewReport()
    {
        var report = Parse(AgentReportWriter.Build(
            Result(), "cheap", "t", DateTimeOffset.Parse("2026-08-31T12:00:00Z")));

        var estimate = RelayCostEstimator.EstimateReport(report);

        Assert.True(estimate.Priced, "the estimator should recognise the tier alias");
        Assert.True(estimate.CostUsd > 0);
        Assert.Equal(3, estimate.Turns);
        Assert.Equal(200, estimate.CachedTokens);
        Assert.Equal(50, estimate.CacheWriteTokens);
    }

    /// <summary>The write lands on disk and parses back.</summary>
    [Fact]
    public async Task TheReport_IsWrittenAndReadsBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var path = Path.Combine(directory, "nested", "stage1-attempt1.report.json");
        try
        {
            await AgentReportWriter.WriteAsync(
                path, AgentReportWriter.Build(Result(), "cheap", "t", DateTimeOffset.UtcNow));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("success", document.RootElement.GetProperty("result")
                .GetProperty("outcome").GetString());
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(directory);
        }
    }

    /// <summary>
    /// The write is atomic: it overwrites an existing report and leaves no temp
    /// file behind, so a reader never catches a half-written one.
    /// </summary>
    [Fact]
    public async Task TheWrite_IsAtomicAndLeavesNoTempFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var path = Path.Combine(directory, "report.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, """{"stale":true}""");

            await AgentReportWriter.WriteAsync(
                path, AgentReportWriter.Build(Result(), "cheap", "t", DateTimeOffset.UtcNow));

            var text = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("stale", text, StringComparison.Ordinal);
            Assert.False(File.Exists(path + ".tmp"), "the temp file should have been moved, not left");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(directory);
        }
    }

    /// <summary>
    /// The timeline carries each call's REAL input context. It used to
    /// fabricate a linear ramp from the stage total, so its last entry equalled
    /// the SUM of every call's input. The cost estimator reads that last entry
    /// as the final context and bills it, so a seven-call stage whose contexts
    /// were mostly cache hits was billed as though the whole sum were fresh
    /// input. A real stage-1 report showed 35,077 there against a true final
    /// context far below it.
    /// </summary>
    [Fact]
    public void TheTimeline_CarriesTheRealPerCallContext()
    {
        var result = Result() with
        {
            Stats = Result().Stats with
            {
                LlmCalls = 3,
                PromptTokens = 3000,
                PromptTokensPerCall = [400, 900, 1700],
            },
        };

        var report = Parse(AgentReportWriter.Build(
            result, "cheap", "the task prompt", DateTimeOffset.UtcNow));

        var contexts = report.GetProperty("timeline").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "llm_call")
            .Select(e => e.GetProperty("prompt_tokens_est").GetInt32())
            .ToList();

        Assert.Equal([400, 900, 1700], contexts);
        // The point of the fix: the last entry is the last call's context, not
        // the stage total.
        Assert.NotEqual(3000, contexts[^1]);
    }

    /// <summary>
    /// Stats with no per-call record still produce a timeline of the right
    /// length, so the archived schema stays readable.
    /// </summary>
    [Fact]
    public void WithNoPerCallRecord_TheTimelineIsStillTheRightLength()
    {
        var report = Parse(AgentReportWriter.Build(
            Result(), "cheap", "the task prompt", DateTimeOffset.UtcNow));

        Assert.Equal(3, report.GetProperty("timeline").EnumerateArray()
            .Count(e => e.GetProperty("type").GetString() == "llm_call"));
    }
}
