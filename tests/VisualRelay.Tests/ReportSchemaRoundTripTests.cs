using System.Text.Json;
using VisualRelay.Core.Agent;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Tasks;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The schema round-trip: a report the new loop writes must drive the same
/// readers, to the same values, as one the subprocess wrote.
/// <para>
/// Four golden reports from the archived corpus sit in Fixtures and were read by
/// nothing. The schema is not free to change — 1109 archived reports and two
/// readers depend on it field for field — so this asserts both readers against
/// both shapes rather than trusting that.
/// </para>
/// </summary>
public sealed class ReportSchemaRoundTripTests
{
    private static string GoldenPath(int stage) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", $"stage{stage}-attempt1.report.json");

    private static JsonElement Golden(int stage) =>
        JsonDocument.Parse(File.ReadAllText(GoldenPath(stage))).RootElement.Clone();

    private static AgentLoopResult Result() =>
        new(
            AgentLoopOutcome.Success,
            """{"summary":"did it","options":["a"]}""",
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
                LlmCalls = 3,
                TotalLlmTimeSeconds = 9.5,
                TotalToolTimeSeconds = 0.75,
                PromptTokens = 9000,
                PromptTokensPerCall = [2000, 3000, 4000],
                CompletionTokens = 400,
                CachedTokens = 1500,
            },
            Error: null,
            ServedModel: "deepseek-v4-flash");

    /// <summary>Every golden report is still readable by the cost estimator.</summary>
    /// <param name="stage">The golden stage number.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryGoldenReport_IsPricedByTheEstimator(int stage)
    {
        var estimate = RelayCostEstimator.EstimateReport(Golden(stage));

        Assert.True(estimate.Priced, $"golden stage {stage} came back unpriced");
        Assert.True(estimate.CostUsd > 0);
        Assert.True(estimate.Turns > 0);
    }

    /// <summary>
    /// A report the new loop writes is priced by the same estimator, reading the
    /// same fields. The uncached input comes off the timeline's last entry in
    /// both shapes.
    /// </summary>
    [Fact]
    public void ANewReport_IsPricedByTheSameEstimator()
    {
        var report = AgentReportWriter.Build(
            Result(), "cheap", "the task", DateTimeOffset.UtcNow);

        var estimate = RelayCostEstimator.EstimateReport(
            JsonDocument.Parse(report.ToJsonString()).RootElement);

        Assert.True(estimate.Priced);
        Assert.Equal(3, estimate.Turns);
        // The final context, not the sum of every call's input.
        Assert.Equal(4000, estimate.PromptTokens);
    }

    /// <summary>
    /// The run history reads a new report into a stage metric, the same way it
    /// reads an archived one. This is the reader the queue's per-task time and
    /// cost columns are built from.
    /// </summary>
    [Fact]
    public void ANewReport_ReadsIntoAStageMetric()
    {
        using var repo = TestRepository.Create();
        var taskDirectory = Path.Combine(repo.Root, ".relay", "a-task");
        Directory.CreateDirectory(taskDirectory);

        var report = AgentReportWriter.Build(
            Result(), "cheap", "the task", DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(taskDirectory, "stage1-attempt1.report.json"), report.ToJsonString());

        var metric = RelayRunHistory.ReadTaskMetric(repo.Root, "a-task");

        var stage = Assert.Single(metric.Stages);
        Assert.Equal(1, stage.StageNumber);
        Assert.True(stage.CostUsd > 0, "the stage metric came back with no cost");
        Assert.True(stage.PromptTokens > 0, "the stage metric came back with no input tokens");
    }

    /// <summary>
    /// An archived report reads into a stage metric too, so both shapes reach
    /// the same reader rather than the new one having its own path.
    /// </summary>
    [Fact]
    public void AnArchivedReport_ReadsIntoAStageMetric()
    {
        using var repo = TestRepository.Create();
        var taskDirectory = Path.Combine(repo.Root, ".relay", "a-task");
        Directory.CreateDirectory(taskDirectory);
        File.Copy(GoldenPath(1), Path.Combine(taskDirectory, "stage1-attempt1.report.json"));

        var metric = RelayRunHistory.ReadTaskMetric(repo.Root, "a-task");

        var stage = Assert.Single(metric.Stages);
        Assert.Equal(1, stage.StageNumber);
        Assert.True(stage.CostUsd > 0);
    }

    /// <summary>
    /// The trace parser produces the same entry sequence, in the same order,
    /// from a trace the new loop writes as from a recorded one. The new path
    /// keeps the recorded format precisely so this holds.
    /// </summary>
    [Fact]
    public void TheTraceParser_ReadsBothShapesIdentically()
    {
        const string recorded = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"thinking about it"}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"read_file","input":{"path":"a.cs"}}]}}
            """;

        // What the new loop emits: the same records, one content block per line
        // rather than batched, which the parser must flatten identically.
        const string written = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"thinking about it"},{"type":"tool_use","name":"read_file","input":{"path":"a.cs"}}]}}
            """;

        var fromRecording = RelayTraceParser.Parse(recorded);
        var fromNew = RelayTraceParser.Parse(written);

        Assert.Equal(
            fromRecording.Select(e => (e.Kind, e.Title)),
            fromNew.Select(e => (e.Kind, e.Title)));
        Assert.Equal(TraceEntryKind.AssistantText, fromNew[0].Kind);
        Assert.Equal(TraceEntryKind.ToolCall, fromNew[1].Kind);
    }

    /// <summary>
    /// A real trace off the archived corpus still parses into entries. The
    /// fixtures above are hand-made; this is the format as it actually occurs.
    /// </summary>
    [Fact]
    public void ARecordedTrace_StillParses()
    {
        var traces = RecordedTrace.Discover(RepoSetup.Root);
        if (traces.Count == 0)
            Assert.Skip("no recorded traces on this machine; the corpus is gitignored run history.");

        var entries = RelayTraceParser.Parse(File.ReadAllText(traces[^1]));

        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Kind == TraceEntryKind.ToolCall);
    }
}
