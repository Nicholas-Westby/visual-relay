using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class NoCommitContaminationTests
{
    [Fact]
    public async Task TwoTasks_ManifestAuthority_EnforcedAcrossPlanExecuteSplit()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("clean", "# Clean\n");
        repo.WriteTask("mixed", "# Mixed manifest\n");

        // One GitSim engine backs Phase 1 worktree creation and Phase 2 commits.
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/shared.cs", "baseline");
        sim.Commit(repo.Root, "seed");

        var cleanInner = new ScriptedSubagentRunner();
        cleanInner.SeedHappyPath("src/clean.cs", "tests/clean.tests.cs");
        var cleanRunner = new FileWritingSubagentRunner(cleanInner, 6, "src/clean.cs", "clean impl");

        var mixedRunner = new FileWritingSubagentRunner(
            new BadManifestSubagentRunner(), 6, "src/real.cs", "real impl");

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
            tasks: [("clean", cleanRunner), ("mixed", mixedRunner)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg,
            gitInvoker: sim);
        Assert.Equal(2, planResults.Count);

        var execOptions = new RelayDriverOptions(CreateGitCommit: true, Resume: true);

        var mixedDriver = new RelayDriver(
            RelayDriverDependencies.ForTests(mixedRunner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            execOptions);
        var mixedOutcome = await mixedDriver.RunTaskAsync(repo.Root, "mixed");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, mixedOutcome.Status);

        var mixedManifest = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "mixed", "manifest.txt"));
        Assert.DoesNotContain("llm-tasks/", mixedManifest, StringComparison.Ordinal);
        Assert.Contains("src/real.cs", mixedManifest, StringComparison.Ordinal);

        var cleanDriver = new RelayDriver(
            RelayDriverDependencies.ForTests(cleanRunner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            execOptions);
        var cleanOutcome = await cleanDriver.RunTaskAsync(repo.Root, "clean");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, cleanOutcome.Status);

        var cleanCommitFiles = sim.FilesChangedInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.DoesNotContain("src/real.cs", cleanCommitFiles);
        Assert.DoesNotContain("llm-tasks/extra.md", cleanCommitFiles);
    }
}
