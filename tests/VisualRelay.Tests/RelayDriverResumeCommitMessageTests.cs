using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A resume that starts at the commit stage commits with the messages Verify wrote. They are read
/// only while Verify runs, so a task whose commit failed once (on the Windows arm, a fresh distro's
/// missing git identity) and was resumed at Commit landed with the generic "chore(relay): task"
/// subject, although the ledger kept Verify's answer with its commit messages.
/// </summary>
public sealed class RelayDriverResumeCommitMessageTests
{
    [Fact]
    public async Task RunTaskAsync_ResumeAtCommit_CommitsWithTheMessagesVerifyWrote()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", []);
        repo.WriteTask("commit-resume", "# Commit resume\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed repo");
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "new");

        var manifest = new[] { "src/app.cs" };
        RelayDriverResumeTestHelpers.SetupCommitGateResumeScenario(
            repo.Root, "commit-resume", manifest, RelayDriverResumeTestHelpers.ComputeTreeHash(repo.Root, manifest));
        var ledgerPath = Path.Combine(repo.Root, ".relay", "commit-resume", "ledger.md");
        File.WriteAllText(ledgerPath, File.ReadAllText(ledgerPath).Replace(
            "body for stage 10",
            "{\n  \"summary\": \"Kept the new status.\",\n  \"commitMessages\": [\n    \"fix(app): keep the new status\",\n    \"fix: keep the new status\"\n  ]\n}",
            StringComparison.Ordinal));

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new ScriptedSubagentRunner(),
                new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink(), sim),
            new RelayDriverOptions(CreateGitCommit: true, Resume: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "commit-resume", TestContext.Current.CancellationToken);

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        Assert.StartsWith("fix(app): keep the new status", sim.CommitInfo(repo.Root, sim.Head(repo.Root)!)!.Message,
            StringComparison.Ordinal);
    }

    /// <summary>A task resumed more than once keeps a Verify section per run; the last one is the answer.</summary>
    [Fact]
    public void CommitMessagesFromLedger_ReadsTheLastVerifySectionAndTheLegacyField()
    {
        const string ledger = "## Stage 10 - Verify\n\n{ \"summary\": \"first\", \"commitMessages\": [\"fix: first run\"] }\n\n"
            + "## Stage 11 - Fix-verify\n\n{ \"summary\": \"fixed\" }\n\n"
            + "## Stage 10 - Verify\n\n{ \"summary\": \"second\", \"commitMessages\": [\"fix: second run\"] }\n\n"
            + "## Stage 11 - Fix-verify\n\n{ \"summary\": \"skipped\" }\n";

        Assert.Equal(["fix: second run"], RelayDriver.CommitMessagesFromLedger(ledger));
        Assert.Equal(["fix: legacy"], RelayDriver.CommitMessagesFromLedger(
            "## Stage 10 - Verify\n\n{ \"summary\": \"old\", \"commitMessage\": \"fix: legacy\" }\n"));
        Assert.Empty(RelayDriver.CommitMessagesFromLedger("## Stage 9 - Fix\n\nbody for stage 9\n"));
    }
}
