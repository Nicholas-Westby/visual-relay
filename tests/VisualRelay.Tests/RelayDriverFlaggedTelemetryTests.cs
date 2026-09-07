using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A flagged stage cost real money and real minutes, and the run has to say so.
/// Before this, the flagged branch wrote a status entry with null cost, duration,
/// turns and model, and emitted no stage trace at all — so a drain's most
/// expensive stages were exactly the ones its ledger could not see.
/// </summary>
public sealed class RelayDriverFlaggedTelemetryTests
{
    /// <summary>
    /// Answers stage 2 with an invalid result, having written the same attempt
    /// report a real stage leaves behind.
    /// </summary>
    private sealed class FlagAfterReport(int stage) : ISubagentRunner
    {
        private readonly ScriptedSubagentRunner _inner = new();

        public Task<SubagentResult> RunAsync(
            StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (invocation.Stage.Number != stage)
                return _inner.RunAsync(invocation, cancellationToken);

            Directory.CreateDirectory(invocation.TraceDirectory);
            StageReportSeed.Write(invocation);
            return Task.FromResult(new SubagentResult(
                RawText: "prose with no contract in it",
                Json: null,
                IsValid: false,
                Error: "the contract block is not valid JSON"));
        }
    }

    private static (RelayDriver Driver, InMemoryRelayEventSink Events) Build(
        TestRepository repo, int flagStage)
    {
        var sim = RelayDriverTestHelpers.InitTestRepo(repo);
        var events = new InMemoryRelayEventSink();
        return (
            new RelayDriver(
                RelayDriverDependencies.ForTests(
                    new FlagAfterReport(flagStage),
                    new ScriptedTestRunner(new TestRunResult(0, "green")),
                    events,
                    sim),
                RelayDriverOptions.NoGitCommit),
            events);
    }

    /// <summary>
    /// The flagged stage's own attempt report is what priced it, so the status
    /// record carries the same cost, turns and model a completed stage would.
    /// </summary>
    [Fact]
    public async Task AFlaggedStage_RecordsItsStats()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("true", []);
        repo.WriteTask("broken-contract", "# Do a thing\n\nGo on then.\n");
        var (driver, _) = Build(repo, flagStage: 2);

        var outcome = await driver.RunTaskAsync(repo.Root, "broken-contract");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        var flagged = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "broken-contract"))
            .Single(e => e.Stage == 2);
        Assert.Equal("Flagged", flagged.Status);
        Assert.NotNull(flagged.CostUsd);
        Assert.NotNull(flagged.DurationSeconds);
        Assert.Equal(2, flagged.Turns);
        Assert.Equal("cheap", flagged.Model);
    }

    /// <summary>
    /// And the run log carries the stage trace, so a drain's cost is summable
    /// from the log rather than only from the status files.
    /// </summary>
    [Fact]
    public async Task AFlaggedStage_TracesLikeAFinishedOne()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("true", []);
        repo.WriteTask("broken-contract", "# Do a thing\n\nGo on then.\n");
        var (driver, events) = Build(repo, flagStage: 2);

        await driver.RunTaskAsync(repo.Root, "broken-contract");

        var done = Assert.Single(
            events.Events, e => e.EventName == "stage_done" && e.StageNumber == 2);
        Assert.Equal("Flagged", done.Data!["status"]);
        Assert.True(double.Parse(done.Data["costUsd"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("cheap", done.Data["model"]);
    }
}
