using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// What a resumed run leaves on record. Measured on the Windows arm with FreshRSS: a task
/// cancelled mid-Implement resumed from its flagged-work bundle and committed, yet run.log
/// said nothing about the restore (only run_start and stage_start), and status.json still
/// gave the stage that passed on resume the error "cancelled by operator".
/// </summary>
public sealed class RelayDriverResumeRecordsTests
{
    [Fact]
    public async Task Resume_LogsThatTheFlaggedWorkWasRestored()
    {
        using var repo = TestRepository.Create();
        var (events, outcome) = await FlagAtImplementThenResumeAsync(repo);

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var restored = Assert.Single(events.Events, e => e.EventName == "flagged_work_restored");
        Assert.Equal(6, restored.StageNumber);
        Assert.Equal("clean", restored.Data!["result"]);
    }

    [Fact]
    public async Task Resume_ClearsTheErrorOfTheStageThatPassesThisTime()
    {
        using var repo = TestRepository.Create();
        var (_, outcome) = await FlagAtImplementThenResumeAsync(repo);

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var implement = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "resume-records"))[5];
        Assert.Equal("Done", implement.Status);
        Assert.Null(implement.Error);
    }

    /// <summary>A committing run that flags at Implement, then a resume of it that commits.</summary>
    private static async Task<(InMemoryRelayEventSink ResumeEvents, RelayTaskOutcome Outcome)> FlagAtImplementThenResumeAsync(
        TestRepository repo)
    {
        repo.WriteConfig("exit 0", [], enableFixVerify: false);
        repo.WriteTask("resume-records", "# Resume records\n");
        var sim = RelayDriverTestHelpers.InitTestRepo(repo);
        var flagged = await new RelayDriver(
            RelayDriverDependencies.ForTests(new FlagAtStageSubagentRunner(flagAtStage: 6),
                new ScriptedTestRunner(new TestRunResult(1, "red")), new InMemoryRelayEventSink(), sim),
            new RelayDriverOptions(CreateGitCommit: true)).RunTaskAsync(repo.Root, "resume-records");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, flagged.Status);
        Assert.NotNull(StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "resume-records"))[5].Error);

        var events = new InMemoryRelayEventSink();
        var outcome = await new RelayDriver(
            RelayDriverDependencies.ForTests(
                new FileWritingSubagentRunner(new ScriptedSubagentRunner(), stage: 6, "src/app.cs", "implemented"),
                new ScriptedTestRunner(new TestRunResult(0, "green")), events, sim),
            new RelayDriverOptions(CreateGitCommit: true, Resume: true)).RunTaskAsync(repo.Root, "resume-records");
        return (events, outcome);
    }
}
