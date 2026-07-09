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

        // Seed a GitSim repo so we can verify commit contents. The same in-memory
        // engine backs Phase 1's worktree creation and Phase 2's commits, so the
        // whole plan → serial-execute history stays coherent without the real binary.
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/shared.cs", "baseline");
        var seedHash = sim.Commit(repo.Root, "chore: seed repo");

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
            MaxPlanConcurrency: 2,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);

        var planResults = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("task-a", runnerA), ("task-b", runnerB)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg,
            gitInvoker: sim);

        Assert.Equal(2, planResults.Count);
        Assert.All(planResults, r => Assert.Equal(RelayTaskOutcomeStatus.Planned, r.Outcome.Status));

        // ── Phase 2: execute serially, committing each ──
        // Task A first
        var executeOptions = new RelayDriverOptions(CreateGitCommit: true, Resume: true);
        var driverA = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerA,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            executeOptions);
        var outcomeA = await driverA.RunTaskAsync(repo.Root, "task-a");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcomeA.Status);

        // Task B second — must commit cleanly even though task A's commit
        // already landed and advanced HEAD.
        var driverB = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerB,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            executeOptions);
        var outcomeB = await driverB.RunTaskAsync(repo.Root, "task-b");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcomeB.Status);

        // ── Verify each commit contains ONLY its own files ──

        // There should be exactly 2 commits on top of the seed (one per task).
        var head = sim.Head(repo.Root)!;
        Assert.Equal(2, sim.CommitsBetween(repo.Root, seedHash, head).Count);

        // Task B's commit (HEAD) must contain only task-b files.
        var headFiles = sim.FilesChangedInCommit(repo.Root, head);
        Assert.Contains("src/b.cs", headFiles);
        Assert.Contains("tests/b.tests.cs", headFiles);
        Assert.Contains(".relay/task-b/manifest.txt", headFiles);
        Assert.Contains(".relay/task-b/ledger.md", headFiles);
        Assert.DoesNotContain("src/a.cs", headFiles);
        Assert.DoesNotContain("tests/a.tests.cs", headFiles);
        Assert.DoesNotContain(headFiles, p => p.StartsWith(".relay/task-a/", StringComparison.Ordinal));

        // Task A's commit (HEAD~1) must contain only task-a files.
        var parent = sim.CommitInfo(repo.Root, head)!.Parents[0];
        var parentFiles = sim.FilesChangedInCommit(repo.Root, parent);
        Assert.Contains("src/a.cs", parentFiles);
        Assert.Contains("tests/a.tests.cs", parentFiles);
        Assert.Contains(".relay/task-a/manifest.txt", parentFiles);
        Assert.Contains(".relay/task-a/ledger.md", parentFiles);
        Assert.DoesNotContain("src/b.cs", parentFiles);
        Assert.DoesNotContain("tests/b.tests.cs", parentFiles);
        Assert.DoesNotContain(parentFiles, p => p.StartsWith(".relay/task-b/", StringComparison.Ordinal));

        // Shared file must NOT appear in either commit (it was not modified).
        Assert.DoesNotContain("src/shared.cs", headFiles);
        Assert.DoesNotContain("src/shared.cs", parentFiles);
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

        // One GitSim engine backs Phase 1 worktree creation and Phase 2 commits.
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/shared.cs", "baseline");
        var seedHash = sim.Commit(repo.Root, "chore: seed");

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
            environmentAccessor: PlanPhaseTestHelpers.TempXdg,
            gitInvoker: sim);
        Assert.Equal(2, planResults.Count);

        // Execute "second" first, then "first" — reversed order to expose any
        // ordering-dependent contamination.
        var execOptions = new RelayDriverOptions(CreateGitCommit: true, Resume: true);
        var driverSecond = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerSecond,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            execOptions);
        var outcomeSecond = await driverSecond.RunTaskAsync(repo.Root, "second");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcomeSecond.Status);

        var driverFirst = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerFirst,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            execOptions);
        var outcomeFirst = await driverFirst.RunTaskAsync(repo.Root, "first");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcomeFirst.Status);

        // Two commits on top of seed.
        var head = sim.Head(repo.Root)!;
        Assert.Equal(2, sim.CommitsBetween(repo.Root, seedHash, head).Count);

        // HEAD (first) must NOT contain second's files.
        var headFiles = sim.FilesChangedInCommit(repo.Root, head);
        Assert.Contains("src/first.cs", headFiles);
        Assert.DoesNotContain("src/second.cs", headFiles);
        Assert.DoesNotContain("tests/second.tests.cs", headFiles);

        // HEAD~1 (second) must NOT contain first's files.
        var parentFiles = sim.FilesChangedInCommit(repo.Root, sim.CommitInfo(repo.Root, head)!.Parents[0]);
        Assert.Contains("src/second.cs", parentFiles);
        Assert.DoesNotContain("src/first.cs", parentFiles);
        Assert.DoesNotContain("tests/first.tests.cs", parentFiles);
    }

    // ── Manifest authority ──────────────────────────────────────────

}
