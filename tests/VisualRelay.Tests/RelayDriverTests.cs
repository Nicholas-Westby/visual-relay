using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Tasks;
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
            await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "add-status", "manifest.txt")));

        var seals = await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".relay", "add-status", "add-status.seals"));
        Assert.Contains(seals, line => line.Contains("\"n\":5", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        Assert.Contains(seals, line => line.Contains("\"n\":9", StringComparison.Ordinal) && line.Contains("\"check\":\"green\"", StringComparison.Ordinal));

        Assert.Contains(sink.Events, e => e.EventName == "stage_start" && e.StageNumber == 1);
        Assert.Contains(sink.Events, e => e.EventName == "stage_done" && e.StageNumber == 11);
        Assert.Contains(
            sink.Events,
            e => e.EventName == "run_start" && e.Data is not null && e.Data["base_url"] == ModelBackend.BaseUrl);
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

    [Fact]
    public async Task RunTaskAsync_PresentationOnlyChange_SkipsRedGateAndCommits()
    {
        // A markup-only change (e.g. de-virtualizing a ListBox in XAML) has no
        // behavior to unit-test, so stage 5 returns no testFiles. The red gate must
        // treat that as not-applicable rather than flagging "author-tests did not
        // go red" — otherwise the pipeline can never complete presentation tasks.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("tweak-markup", "# Tweak markup\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "Panel.axaml"), "<Panel/>");
        var runner = new ScriptedSubagentRunner();
        runner.SeedPresentationOnly("src/Panel.axaml");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "tweak-markup");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    [Fact]
    public async Task RunTaskAsync_FlagsUnexpectedRunnerCrashAndSkipsFuturePendingLists()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("crashy", "# Crashy\n");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new ThrowingSubagentRunner(), new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "crashy");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        var review = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "crashy", "NEEDS-REVIEW"));
        Assert.Contains("exception: kaboom", review, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowingSubagentRunner), review, StringComparison.Ordinal);
        Assert.Empty(await new RelayTaskRepository(repo.Root).ListPendingAsync());
    }

    [Fact]
    public async Task RunTaskAsync_AllocatesNextAttemptIndexOnEachReRun()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("re-run", "# Re-run\n");

        await RunHappyPath(repo, "re-run");
        await RunHappyPath(repo, "re-run");
        await RunHappyPath(repo, "re-run");

        var taskDirectory = Path.Combine(repo.Root, ".relay", "re-run");
        // First run -> attempt1, each re-run -> attempt2, attempt3. Stage 1 is a subagent stage.
        // The artifact-writing runner materializes the report/trace at the paths the driver chose,
        // so existence here proves the driver threaded an incrementing index (not a fixed attempt1).
        Assert.True(File.Exists(Path.Combine(taskDirectory, "stage1-attempt1.report.json")));
        Assert.True(File.Exists(Path.Combine(taskDirectory, "stage1-attempt2.report.json")));
        Assert.True(File.Exists(Path.Combine(taskDirectory, "stage1-attempt3.report.json")));
        Assert.True(Directory.Exists(Path.Combine(taskDirectory, "stage1-attempt1")));
        Assert.True(Directory.Exists(Path.Combine(taskDirectory, "stage1-attempt2")));
        Assert.True(Directory.Exists(Path.Combine(taskDirectory, "stage1-attempt3")));
    }

    private static async Task RunHappyPath(TestRepository repo, string taskId)
    {
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        var outcome = await driver.RunTaskAsync(repo.Root, taskId);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    [Fact]
    public async Task RunTaskAsync_RetriesTaskThatWasAlreadyNeedsReview()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("retry-me", "# Retry\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay", "retry-me"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "retry-me", "NEEDS-REVIEW"), "old failure\n");
        var runner = new ScriptedSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "retry-me");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "retry-me", "NEEDS-REVIEW")));
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

internal sealed class ArtifactWritingSubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _scripted = new();

    public void SeedHappyPath(string codeFile, string testFile) => _scripted.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        // Mimic Swival: materialize the trace dir + a session file and the report at the
        // exact paths the driver allocated, so re-runs surface as distinct attempt artifacts.
        Directory.CreateDirectory(invocation.TraceDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(invocation.TraceDirectory, $"{Guid.NewGuid():N}.jsonl"),
            """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""",
            cancellationToken);
        await File.WriteAllTextAsync(
            invocation.ReportFile,
            """{ "model": "cheap-kimi", "result": { "outcome": "ok" }, "stats": {}, "timeline": [] }""",
            cancellationToken);
        return await _scripted.RunAsync(invocation, cancellationToken);
    }
}

internal sealed class ThrowingSubagentRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("kaboom");
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
