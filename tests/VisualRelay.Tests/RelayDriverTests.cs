using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverTests
{
    [Fact]
    public async Task RunTaskAsync_WritesLedgerSealsManifestAndStructuredEvents()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("add-status", "# Add status\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "add-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay", "ACTIVE")));
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "add-status", "ledger.md")));
        Assert.Equal(
            $"src/status.cs{Environment.NewLine}tests/status.tests.cs{Environment.NewLine}",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "add-status", "manifest")));

        var seals = await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".relay", "add-status", "add-status.seals"));
        Assert.Contains(seals, line => line.Contains("\"n\":5", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        Assert.Contains(seals, line => line.Contains("\"n\":9", StringComparison.Ordinal) && line.Contains("\"check\":\"green\"", StringComparison.Ordinal));

        Assert.Contains(sink.Events, e => e.EventName == "stage_start" && e.StageNumber == 1);
        Assert.Contains(sink.Events, e => e.EventName == "stage_done" && e.StageNumber == 11);
    }
}

