using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class NoCommitContaminationTests
{
    /// <summary>
    /// THE KEY REGRESSION TEST: Two tasks are planned concurrently, then executed
    /// serially. Each resulting commit must contain ONLY its own manifest/authored
    /// files — never the other task's files. This guards against the scenario
    /// where `git add -u` silently stages another task's uncommitted edits.
    /// </summary>
    [Fact]
    public async Task TwoTasks_PlanThenExecute_EachCommitContainsOnlyItsOwnFiles()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task-a", "# Task A\n");
        repo.WriteTask("task-b", "# Task B\n");

        // Seed a git repo so we can verify commit contents.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "shared.cs"), "baseline");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@test.example");
        TestGit.Run(repo.Root, "config", "user.name", "VR Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");
        var seedHash = TestGit.Run(repo.Root, "rev-parse", "HEAD").Trim();

        // ── Phase 1: plan both tasks in parallel ──
        var runnerA = new DualTaskSubagentRunner("task-a", "src/a.cs", "tests/a.tests.cs");
        var runnerB = new DualTaskSubagentRunner("task-b", "src/b.cs", "tests/b.tests.cs");

        var config = new RelayConfig(
            TasksDir: "llm-tasks",
            TestCommand: "dotnet test",
            TestFileCommand: "dotnet test {files}",
            LogSources: [],
            TierProfiles: new Dictionary<string, string>(),
            EnableFixVerify: true,
            MaxStageFailures: 3,
            MaxTurns: 200,
            BaselineVerify: true,
            ArchiveOnDone: true,
            SubagentTimeoutMilliseconds: 1_200_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>(),
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2,
            MaxPlanConcurrency: 2,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);

        var planResults = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("task-a", runnerA), ("task-b", runnerB)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);

        Assert.Equal(2, planResults.Count);
        Assert.All(planResults, r => Assert.Equal(RelayTaskOutcomeStatus.Planned, r.Outcome.Status));

        // ── Phase 2: execute serially, committing each ──
        // Task A first
        var executeOptions = new RelayDriverOptions(CreateGitCommit: true, Resume: true);
        var driverA = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerA,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            executeOptions);
        var outcomeA = await driverA.RunTaskAsync(repo.Root, "task-a");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcomeA.Status);

        // Task B second — must commit cleanly even though task A's commit
        // already landed and advanced HEAD.
        var driverB = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerB,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            executeOptions);
        var outcomeB = await driverB.RunTaskAsync(repo.Root, "task-b");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcomeB.Status);

        // ── Verify each commit contains ONLY its own files ──

        // There should be exactly 2 commits on top of the seed (one per task).
        var commitCount = TestGit.Run(repo.Root, "rev-list", "--count", $"{seedHash}..HEAD").Trim();
        Assert.Equal("2", commitCount);

        // Task B's commit (HEAD) must contain only task-b files.
        var headFiles = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("src/b.cs", headFiles, StringComparison.Ordinal);
        Assert.Contains("tests/b.tests.cs", headFiles, StringComparison.Ordinal);
        Assert.Contains(".relay/task-b/manifest.txt", headFiles, StringComparison.Ordinal);
        Assert.Contains(".relay/task-b/ledger.md", headFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("src/a.cs", headFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("tests/a.tests.cs", headFiles, StringComparison.Ordinal);
        Assert.DoesNotContain(".relay/task-a/", headFiles, StringComparison.Ordinal);

        // Task A's commit (HEAD~1) must contain only task-a files.
        var parentFiles = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD~1");
        Assert.Contains("src/a.cs", parentFiles, StringComparison.Ordinal);
        Assert.Contains("tests/a.tests.cs", parentFiles, StringComparison.Ordinal);
        Assert.Contains(".relay/task-a/manifest.txt", parentFiles, StringComparison.Ordinal);
        Assert.Contains(".relay/task-a/ledger.md", parentFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("src/b.cs", parentFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("tests/b.tests.cs", parentFiles, StringComparison.Ordinal);
        Assert.DoesNotContain(".relay/task-b/", parentFiles, StringComparison.Ordinal);

        // Shared file must NOT appear in either commit (it was not modified).
        Assert.DoesNotContain("src/shared.cs", headFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("src/shared.cs", parentFiles, StringComparison.Ordinal);
    }

    /// <summary>
    /// When two tasks are planned then executed, the first task's commit must
    /// not include any untracked files authored by the second task's planning
    /// phase (e.g. test files created in stage 5). The pre-run untracked
    /// snapshot + auto-include must not cross-contaminate.
    /// </summary>
    [Fact]
    public async Task TwoTasks_FirstCommitDoesNotIncludeSecondTasksUntrackedFiles()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("first", "# First\n");
        repo.WriteTask("second", "# Second\n");

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "shared.cs"), "baseline");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@test.example");
        TestGit.Run(repo.Root, "config", "user.name", "VR Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed");
        var seedHash = TestGit.Run(repo.Root, "rev-parse", "HEAD").Trim();

        // Both tasks use DualTaskSubagentRunner which creates test files at
        // stage 5 and impl files at stage 6.
        var runnerFirst = new DualTaskSubagentRunner("first", "src/first.cs", "tests/first.tests.cs");
        var runnerSecond = new DualTaskSubagentRunner("second", "src/second.cs", "tests/second.tests.cs");

        var config = new RelayConfig(
            TasksDir: "llm-tasks",
            TestCommand: "dotnet test",
            TestFileCommand: "dotnet test {files}",
            LogSources: [],
            TierProfiles: new Dictionary<string, string>(),
            EnableFixVerify: true,
            MaxStageFailures: 3,
            MaxTurns: 200,
            BaselineVerify: true,
            ArchiveOnDone: true,
            SubagentTimeoutMilliseconds: 1_200_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>(),
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2,
            MaxPlanConcurrency: 2,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);

        // Plan both.
        var planResults = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("first", runnerFirst), ("second", runnerSecond)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);
        Assert.Equal(2, planResults.Count);

        // Execute "second" first, then "first" — reversed order to expose any
        // ordering-dependent contamination.
        var execOptions = new RelayDriverOptions(CreateGitCommit: true, Resume: true);
        var driverSecond = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerSecond,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            execOptions);
        var outcomeSecond = await driverSecond.RunTaskAsync(repo.Root, "second");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcomeSecond.Status);

        var driverFirst = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerFirst,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            execOptions);
        var outcomeFirst = await driverFirst.RunTaskAsync(repo.Root, "first");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcomeFirst.Status);

        // Two commits on top of seed.
        Assert.Equal("2", TestGit.Run(repo.Root, "rev-list", "--count", $"{seedHash}..HEAD").Trim());

        // HEAD (first) must NOT contain second's files.
        var headFiles = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("src/first.cs", headFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("src/second.cs", headFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("tests/second.tests.cs", headFiles, StringComparison.Ordinal);

        // HEAD~1 (second) must NOT contain first's files.
        var parentFiles = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD~1");
        Assert.Contains("src/second.cs", parentFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("src/first.cs", parentFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("tests/first.tests.cs", parentFiles, StringComparison.Ordinal);
    }
}
