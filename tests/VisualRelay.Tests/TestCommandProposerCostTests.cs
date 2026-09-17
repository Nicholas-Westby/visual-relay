using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Bootstrap's proposer spends real provider money, and the running total only counts
/// what arrives on a <c>stage_done</c>. Measured on a Windows box: a 15-turn,
/// 24-tool-call proposer ran for 96 seconds against the cheap tier and
/// <c>/state.sessionCostUsd</c> read 0 before, during and after, because the proposer
/// is not one of the twelve stages and published no such event. An operator reading
/// that field, or a script gating on it, was told nothing had been spent.
/// </summary>
public sealed class TestCommandProposerCostTests
{
    private const string Report = """
        {
          "model": "cheap",
          "served_model": "deepseek-v4-flash",
          "result": { "answer": "done" },
          "stats": {
            "total_llm_time_s": 9.0,
            "total_tool_time_s": 1.0,
            "prompt_cache": { "cached_tokens": 0, "cache_write_tokens": 0 }
          },
          "timeline": [
            { "type": "llm_call", "prompt_tokens_est": 20000 },
            { "type": "llm_call", "prompt_tokens_est": 40000 }
          ]
        }
        """;

    private static RelayConfig Config() =>
        RelayConfigLoader.Defaults(ProjectBootstrapper.PlaceholderTestCommand);

    /// <summary>Writes the report the real runner would have written, then answers.</summary>
    private sealed class ReportingRunner(string? json, bool throws = false) : ISubagentRunner
    {
        public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            File.WriteAllText(invocation.ReportFile, Report);
            if (throws)
                throw new InvalidOperationException("the provider hung up");
            return Task.FromResult(json is null
                ? new SubagentResult(string.Empty, null, false, "no answer")
                : new SubagentResult(json, json, true, null));
        }
    }

    private sealed class CollectingSink : IRelayEventSink
    {
        public List<RelayEvent> Events { get; } = [];

        public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(relayEvent);
            return Task.CompletedTask;
        }
    }

    private static RelayEvent CostEvent(CollectingSink sink) =>
        Assert.Single(sink.Events, e => e.EventName == "stage_done");

    [Fact]
    public async Task RunAsync_ReportsWhatTheProposerCost()
    {
        using var repo = TestRepository.Create();
        var sink = new CollectingSink();

        await TestCommandProposer.RunAsync(
            repo.Root, [], Config(),
            new ReportingRunner("""{"testCmd":"go test ./...","evidence":"the CI workflow"}"""),
            CancellationToken.None, sink);

        var done = CostEvent(sink);
        Assert.Equal("TestCommandProposer", done.TaskId);
        Assert.Equal("cheap", done.Tier);
        var costUsd = Assert.Contains("costUsd", done.Data!);
        Assert.True(double.Parse(costUsd, System.Globalization.CultureInfo.InvariantCulture) > 0);
        // The tier alias, exactly as the driver's own stage_done reports it.
        Assert.Equal("cheap", Assert.Contains("model", done.Data!));
    }

    [Fact]
    public async Task RunPerFileAsync_ReportsWhatTheProposerCost()
    {
        using var repo = TestRepository.Create();
        var sink = new CollectingSink();

        await TestCommandProposer.RunPerFileAsync(
            repo.Root, "go test ./...", ["main_test.go"], null, Config(),
            new ReportingRunner("""{"testFileCmd":"go test {files}","evidence":"tried it"}"""),
            CancellationToken.None, sink);

        Assert.Equal("PerFileCommandProposer", CostEvent(sink).TaskId);
    }

    /// <summary>
    /// The provider is paid for a run that answered nothing exactly as for one that
    /// answered, so a failed proposer is the case where a silent zero misleads most.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenTheRunnerThrows_TheCostIsStillReported()
    {
        using var repo = TestRepository.Create();
        var sink = new CollectingSink();

        var proposed = await TestCommandProposer.RunAsync(
            repo.Root, [], Config(), new ReportingRunner(null, throws: true), CancellationToken.None, sink);

        Assert.Null(proposed);
        Assert.NotNull(CostEvent(sink));
    }

    /// <summary>A run that wrote no report reports nothing, rather than a zero.</summary>
    [Fact]
    public async Task RunAsync_WithNoReport_PublishesNothing()
    {
        using var repo = TestRepository.Create();
        var sink = new CollectingSink();

        await TestCommandProposer.RunAsync(
            repo.Root, [], Config(), new ThrowingRunnerWithoutAReport(), CancellationToken.None, sink);

        Assert.DoesNotContain(sink.Events, e => e.EventName == "stage_done");
    }

    private sealed class ThrowingRunnerWithoutAReport : ISubagentRunner
    {
        public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no provider key");
    }
}
