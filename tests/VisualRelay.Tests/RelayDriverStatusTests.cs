using System.Text.Json;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverStatusTests
{
    [Fact]
    public async Task RunTaskAsync_WritesStatusJson_AllStagesDone()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("status-test", "# Status test\n");
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "status-test");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var statusPath = Path.Combine(repo.Root, ".relay", "status-test", "status.json");
        Assert.True(File.Exists(statusPath));

        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "status-test"));
        Assert.Equal(11, entries.Count);
        Assert.All(entries, e => Assert.Equal("Done", e.Status));
    }

    [Fact]
    public async Task RunTaskAsync_StatusJson_CommitStageHasZeroCostNoTurnsNoModel()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("commit-zero", "# Commit zero\n");
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "commit-zero");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "commit-zero"));
        var commitEntry = entries.Single(e => e.Stage == 11);
        Assert.Equal("Commit", commitEntry.Name);
        Assert.Equal("Done", commitEntry.Status);
        Assert.Equal(0, commitEntry.CostUsd);
        Assert.Null(commitEntry.Turns);
        Assert.Null(commitEntry.Model);
    }

    [Fact]
    public async Task RunTaskAsync_StatusJson_Stages5And9HaveCheck()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("checks-test", "# Checks test\n");
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "checks-test");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "checks-test"));
        var stage5 = entries.Single(e => e.Stage == 5);
        Assert.Equal("red", stage5.Check);
        var stage9 = entries.Single(e => e.Stage == 9);
        Assert.Equal("green", stage9.Check);
    }

    [Fact]
    public async Task RunTaskAsync_StatusJson_FlaggedStageHasError()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("flag-error", "# Flag error\n");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new ThrowingSubagentRunner(), new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "flag-error");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "flag-error"));
        Assert.NotEmpty(entries);
        var flagged = entries.Single(e => e.Status == "Flagged");
        Assert.NotNull(flagged.Error);
        Assert.Contains("exception", flagged.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTaskAsync_StatusJson_FlaggedAtMidPipeline_EarlierDoneLaterWaiting()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("mid-flag", "# Mid flag\n");
        // Stage 1-3 pass, stage 4 returns a bad manifest → flags
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new BadManifestSubagentRunner(), new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "mid-flag");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "mid-flag"));
        Assert.NotEmpty(entries);
        // Stage 4 should be flagged
        var stage4 = entries.Single(e => e.Stage == 4);
        Assert.Equal("Flagged", stage4.Status);
        Assert.NotNull(stage4.Error);
        // Stages 1-3 should be done
        Assert.All(entries.Where(e => e.Stage < 4), e => Assert.Equal("Done", e.Status));
        // Stages 5-11 should be waiting
        Assert.All(entries.Where(e => e.Stage > 4), e => Assert.Equal("Waiting", e.Status));
    }

    [Fact]
    public async Task RunTaskAsync_StatusJson_InProofFilesArray()
    {
        // Verify that status.json exists after a committed run.
        // The proof files array includes status.json alongside ledger.md, *.seals, and manifest.txt.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("proof-status", "# Proof status\n");
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "proof-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "proof-status", "status.json")));
    }
}
