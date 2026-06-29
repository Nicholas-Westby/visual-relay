using System.Globalization;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// When a main-loop stage (1–9) escalates IN-PROCESS inside
/// <see cref="SwivalSubagentRunner.RunAsync"/>, the escalated runs each write a
/// distinct <c>stage{n}-attempt{k}.report.json</c>. The driver emits a single
/// <c>stage_done</c> for that stage, which must carry the turns + cost SUMMED
/// across every attempt (so it matches the archived
/// <see cref="RelayRunHistory.SquashAttempts"/> on reload), and
/// <c>sessionCostUsd</c> must include every escalated run — not just the first
/// attempt the start invocation pointed at.
/// </summary>
public sealed class RelayDriverCumulativeCostTests
{
    [Fact]
    public async Task RunTaskAsync_InProcessEscalatedStage_StageDoneCarriesCumulativeTurnsCostAndSessionCost_MatchingArchivedSquash()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 0);
        repo.WriteTask("escalated", "# Escalated stage\n");
        // Stage 1 escalates in-process: RunAsync leaves THREE attempt reports
        // (2, 3, 5 turns → cumulative 10). No other stage writes a report, so the
        // running session cost equals stage 1's own cumulative at its stage_done.
        var runner = new MultiAttemptReportSubagentRunner(escalatingStage: 1, attemptTurns: [2, 3, 5]);
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),     // stage 5 author gate
            new TestRunResult(0, "green"));  // stage 9 verify — green (skips fix-verify)
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "escalated");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var stageDone = sink.Events.Single(e => e is { EventName: "stage_done", StageNumber: 1 });
        var squash = RelayRunHistory.ReadTaskMetric(repo.Root, "escalated").Stages
            .Single(s => s.StageNumber == 1);

        // Turns SUMMED across all three attempts (2 + 3 + 5 = 10), not just attempt 1 (2).
        Assert.Equal("10", stageDone.Data!["turns"]);
        // Live stage_done == archived squash (same files, same summation) for turns + cost.
        Assert.Equal(squash.Turns, int.Parse(stageDone.Data["turns"]));
        Assert.Equal(
            squash.CostUsd,
            double.Parse(stageDone.Data["costUsd"], CultureInfo.InvariantCulture),
            precision: 10);
        Assert.True(squash.CostUsd > 0, "the priced balanced-model reports must yield a non-zero cost");
        // sessionCostUsd includes ALL escalated runs: stage 1 is the only priced stage,
        // so the running total at its stage_done equals its cumulative (no attempt-1 undercount).
        Assert.Equal(MoneyFormatter.Dollars(squash.CostUsd), stageDone.Data["sessionCost"]);
    }
}

/// <summary>
/// Simulates an in-process-escalated stage: for the designated stage, RunAsync
/// writes MULTIPLE <c>stage{n}-attempt{k}.report.json</c> files (one per simulated
/// escalation run, with the given per-run turn counts and a priced model) — exactly
/// what the real <see cref="SwivalSubagentRunner"/> leaves behind when it escalates
/// internally — then returns a valid result via the canned
/// <see cref="ScriptedSubagentRunner"/>. Every other stage writes no report.
/// </summary>
internal sealed class MultiAttemptReportSubagentRunner(int escalatingStage, int[] attemptTurns) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();

    public void SeedHappyPath(string codeFile, string testFile) => _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == escalatingStage)
        {
            var dir = Path.GetDirectoryName(invocation.ReportFile)!;
            Directory.CreateDirectory(dir);
            for (var i = 0; i < attemptTurns.Length; i++)
            {
                var path = Path.Combine(dir, $"stage{invocation.Stage.Number}-attempt{i + 1}.report.json");
                await File.WriteAllTextAsync(path, BuildReport(attemptTurns[i]), cancellationToken);
            }
        }

        return await _inner.RunAsync(invocation, cancellationToken);
    }

    // A minimal priced (balanced-model) report whose llm_call count equals the
    // wanted turn count, so RelayCostEstimator yields known turns + a non-zero cost.
    // Built by concatenation to avoid raw-string brace escaping in the JSON body.
    private static string BuildReport(int turns)
    {
        var calls = string.Join(",", Enumerable.Range(1, turns)
            .Select(i => "{\"type\":\"llm_call\",\"prompt_tokens_est\":" + (i * 1000) + "}"));
        return "{\"model\":\"balanced\",\"result\":{\"answer\":\"ok\"}," +
               "\"stats\":{\"total_llm_time_s\":1.0,\"total_tool_time_s\":0.5," +
               "\"prompt_cache\":{\"cached_tokens\":0,\"cache_write_tokens\":0}}," +
               "\"timeline\":[" + calls + "]}";
    }
}
