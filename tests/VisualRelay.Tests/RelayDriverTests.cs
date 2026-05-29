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

    [Fact]
    public async Task RunTaskAsync_StripsPrematureImplementationBeforeAuthorTestGate()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", []);
        repo.WriteTask("repair-status", "# Repair status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.txt"), "old\n");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");

        var testRunner = new RedGateObservingTestRunner(repo.Root);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new PrematureImplementationRunner(), testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "repair-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Equal(["old", "new"], testRunner.StatusSnapshots);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(repo.Root, "src", "status.txt")));
        Assert.DoesNotContain("relay-redgate:repair-status:", TestGit.Run(repo.Root, "stash", "list"));
    }
}

internal sealed class PrematureImplementationRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 4)
        {
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.txt"), "new\n");
        }

        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "status.test"), "expects new status");
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit status","manifest":["src/status.txt","tests/status.test","src/ghost.txt"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implementation already present"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"no changes"}""",
            9 => """{"summary":"verified"}""",
            10 => """{"summary":"no changes"}""",
            _ => """{"summary":"ok"}"""
        };

        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

internal sealed class RedGateObservingTestRunner : ITestRunner
{
    private readonly string _rootPath;

    public RedGateObservingTestRunner(string rootPath)
    {
        _rootPath = rootPath;
    }

    public List<string> StatusSnapshots { get; } = [];

    public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        Assert.Equal(_rootPath, rootPath);
        var status = File.ReadAllText(Path.Combine(rootPath, "src", "status.txt")).Trim();
        StatusSnapshots.Add(status);
        return Task.FromResult(command == "full-suite"
            ? new TestRunResult(status == "new" ? 0 : 1, status)
            : new TestRunResult(status == "old" ? 1 : 0, status));
    }
}
