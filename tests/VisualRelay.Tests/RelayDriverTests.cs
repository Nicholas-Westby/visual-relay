using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;
using static VisualRelay.Tests.GitSimTestHelpers;

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
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "add-status");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay", "ACTIVE")));
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "add-status", "ledger.md")));
        Assert.Equal(
            $"src/status.cs{Environment.NewLine}tests/status.tests.cs{Environment.NewLine}",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "add-status", "manifest.txt")));

        var seals = await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".relay", "add-status", "add-status.seals"));
        Assert.Contains(seals, line => line.Contains("\"n\":5", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        Assert.Contains(seals, line => line.Contains("\"n\":10", StringComparison.Ordinal) && line.Contains("\"check\":\"green\"", StringComparison.Ordinal));
        Assert.Contains(sink.Events, e => e is { EventName: "stage_start", StageNumber: 1 });
        Assert.Contains(sink.Events, e => e is { EventName: "stage_done", StageNumber: 12 });
        var stage12Done = sink.Events.Single(e => e is { EventName: "stage_done", StageNumber: 12 });
        Assert.False(stage12Done.Data?.ContainsKey("turns"));
        Assert.Contains(sink.Events, e => e is { EventName: "run_start", Data: not null } && e.Data["base_url"] == ModelBackend.BaseUrl);
    }

    [Fact]
    public async Task RunTaskAsync_StripsPrematureImplementationBeforeAuthorTestGate()
    {
        // WorktreeFilter discards non-testFiles edits after stage 5, so the
        // production file stays at HEAD through the red-gate. The red-gate
        // sees a clean implementation file (no stash needed), tests fail red
        // (compile-red is still red), and stage 6 (Implement) starts from a
        // clean base — correct TDD flow.
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", []);
        repo.WriteTask("repair-status", "# Repair status\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");
        var testRunner = new RedGateObservingTestRunner(repo.Root);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new PrematureImplementationRunner(), testRunner, new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "repair-status");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // Stage 5 snapshot: red-gate reads reverted production file = "old" → red.
        // Stage 10 snapshot: after stage 6 implements, status = "new" → green.
        Assert.Equal(["old", "new"], testRunner.StatusSnapshots);
        // Production file was implemented correctly at stage 6.
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(repo.Root, "src", "status.cs")));
        Assert.DoesNotContain("relay-redgate:repair-status:", (await sim.Git(repo.Root, "stash", "list")).Output);
    }

    [Fact]
    public async Task RunTaskAsync_NonCodeOnlyChange_SkipsRedGateAndCommits()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("update-readme", "# Update README\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "docs"));
        File.WriteAllText(Path.Combine(repo.Root, "docs", "README.md"), "# Title\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedNonCodeOnly("docs/README.md");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "update-readme");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    [Fact]
    public async Task RunTaskAsync_FlagsUnexpectedRunnerCrashAndSkipsFuturePendingLists()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("crashy", "# Crashy\n");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new ThrowingSubagentRunner(), new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "crashy");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        var review = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "crashy", "NEEDS-REVIEW"));
        Assert.Contains("exception: kaboom", review, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowingSubagentRunner), review, StringComparison.Ordinal);
        Assert.Empty(await new RelayTaskRepository(repo.Root).ListPendingAsync());
    }
}
