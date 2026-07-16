using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class NoCommitContaminationTests
{
    public enum NoCommitContaminationTestCase
    {
        /// <summary>Original: TwoTasks_PlanThenExecute_EachCommitContainsOnlyItsOwnFiles</summary>
        PlanThenExecute,
        /// <summary>Original: TwoTasks_FirstCommitDoesNotIncludeSecondTasksUntrackedFiles</summary>
        FirstCommitDoesNotIncludeUntracked,
        /// <summary>Original: TwoTasks_ManifestAuthority_EnforcedAcrossPlanExecuteSplit</summary>
        ManifestAuthority,
    }

    public static IEnumerable<object[]> NoCommitContaminationData()
    {
        // Data row 0: PlanThenExecute — task-a first, task-b second
        yield return new object[] { "task-a", "task-b", "chore: seed repo", NoCommitContaminationTestCase.PlanThenExecute };
        // Data row 1: FirstCommitDoesNotIncludeUntracked — second first, first second (reversed order)
        yield return new object[] { "second", "first", "chore: seed", NoCommitContaminationTestCase.FirstCommitDoesNotIncludeUntracked };
        // Data row 2: ManifestAuthority — mixed first, clean second
        yield return new object[] { "mixed", "clean", "seed", NoCommitContaminationTestCase.ManifestAuthority };
    }

    /// <summary>
    /// THE KEY REGRESSION TEST: Two tasks are planned concurrently, then executed
    /// serially. Each resulting commit must contain ONLY its own manifest/authored
    /// files — never the other task's files. This guards against the scenario
    /// where `git add -u` silently stages another task's uncommitted edits.
    ///
    /// Data-driven to share the expensive arrange across three original test
    /// methods: PlanThenExecute (row 0), FirstCommitDoesNotIncludeUntracked (row 1),
    /// and ManifestAuthority (row 2). Each row provides task identifiers, the seed
    /// commit message, and a discriminator that selects runner construction,
    /// execution order, and per-case assertions.
    /// </summary>
    [Theory]
    [MemberData(nameof(NoCommitContaminationData))]
    public async Task NoCommitContamination_TwoTasks_EachCommitContainsOnlyItsOwnFiles(
        string taskIdA, string taskIdB, string seedMessage, NoCommitContaminationTestCase testCase)
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask(taskIdA, $"# {taskIdA}\n");
        repo.WriteTask(taskIdB, $"# {taskIdB}\n");

        // Seed a GitSim repo so we can verify commit contents. The same in-memory
        // engine backs Phase 1's worktree creation and Phase 2's commits, so the
        // whole plan → serial-execute history stays coherent without the real binary.
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/shared.cs", "baseline");
        var seedHash = sim.Commit(repo.Root, seedMessage);

        // ── Phase 1: plan both tasks in parallel ──
        ISubagentRunner runnerA, runnerB;
        switch (testCase)
        {
            case NoCommitContaminationTestCase.PlanThenExecute:
                runnerA = new DualTaskSubagentRunner("task-a", "src/a.cs", "tests/a.tests.cs");
                runnerB = new DualTaskSubagentRunner("task-b", "src/b.cs", "tests/b.tests.cs");
                break;
            case NoCommitContaminationTestCase.FirstCommitDoesNotIncludeUntracked:
                // Both tasks use DualTaskSubagentRunner which creates test files at
                // stage 5 and impl files at stage 6.
                runnerA = new DualTaskSubagentRunner("second", "src/second.cs", "tests/second.tests.cs");
                runnerB = new DualTaskSubagentRunner("first", "src/first.cs", "tests/first.tests.cs");
                break;
            case NoCommitContaminationTestCase.ManifestAuthority:
                {
                    var cleanInner = new ScriptedSubagentRunner();
                    cleanInner.SeedHappyPath("src/clean.cs", "tests/clean.tests.cs");
                    runnerB = new FileWritingSubagentRunner(cleanInner, 6, "src/clean.cs", "clean impl");
                    runnerA = new FileWritingSubagentRunner(
                        new BadManifestSubagentRunner(), 6, "src/real.cs", "real impl");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(testCase));
        }

        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 2);

        var planResults = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [(taskIdA, runnerA), (taskIdB, runnerB)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg,
            gitInvoker: sim);

        switch (testCase)
        {
            case NoCommitContaminationTestCase.PlanThenExecute:
                Assert.Equal(2, planResults.Count);
                Assert.All(planResults, r => Assert.Equal(RelayTaskOutcomeStatus.Planned, r.Outcome.Status));
                break;
            case NoCommitContaminationTestCase.FirstCommitDoesNotIncludeUntracked:
            case NoCommitContaminationTestCase.ManifestAuthority:
                Assert.Equal(2, planResults.Count);
                break;
        }

        // ── Phase 2: execute serially, committing each ──
        var execOptions = new RelayDriverOptions(CreateGitCommit: true, Resume: true);

        // Run taskIdA first.
        var driverA = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerA,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            execOptions);
        var outcomeA = await driverA.RunTaskAsync(repo.Root, taskIdA);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcomeA.Status);

        // ManifestAuthority: read the just-written manifest before running the clean task.
        if (testCase == NoCommitContaminationTestCase.ManifestAuthority)
        {
            var mixedManifest = await File.ReadAllTextAsync(
                Path.Combine(repo.Root, ".relay", "mixed", "manifest.txt"));
            Assert.DoesNotContain("llm-tasks/", mixedManifest, StringComparison.Ordinal);
            Assert.Contains("src/real.cs", mixedManifest, StringComparison.Ordinal);
        }

        // Run taskIdB second.
        var driverB = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerB,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            execOptions);
        var outcomeB = await driverB.RunTaskAsync(repo.Root, taskIdB);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcomeB.Status);

        // ── Per-case commit-content assertions ──

        switch (testCase)
        {
            case NoCommitContaminationTestCase.PlanThenExecute:
                {
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
                    break;
                }
            case NoCommitContaminationTestCase.FirstCommitDoesNotIncludeUntracked:
                {
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
                    break;
                }
            case NoCommitContaminationTestCase.ManifestAuthority:
                {
                    var cleanCommitFiles = sim.FilesChangedInCommit(repo.Root, sim.Head(repo.Root)!);
                    Assert.DoesNotContain("src/real.cs", cleanCommitFiles);
                    Assert.DoesNotContain("llm-tasks/extra.md", cleanCommitFiles);
                    break;
                }
        }
    }
}
