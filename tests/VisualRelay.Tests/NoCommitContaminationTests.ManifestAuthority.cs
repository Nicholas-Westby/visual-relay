using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// Manifest-authority test, split out of NoCommitContaminationTests.cs to keep
// each file under the 300-line guard. Uses helpers from the main partial class.
public sealed partial class NoCommitContaminationTests
{
    /// <summary>
    /// The enforce-commit-authority behavior must survive the plan/execute split:
    /// a task whose manifest lists files under llm-tasks/ must have those entries
    /// dropped during planning, and the commit must not include task files from
    /// another task's directory despite the parallel planning phase.
    /// </summary>
    [Fact]
    public async Task TwoTasks_ManifestAuthority_EnforcedAcrossPlanExecuteSplit()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("clean", "# Clean\n");
        repo.WriteTask("mixed", "# Mixed manifest\n"); // manifest lists llm-tasks/ entry

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "shared.cs"), "baseline");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        // Clean task: normal manifest
        var cleanRunner = new ScriptedSubagentRunner();
        cleanRunner.SeedHappyPath("src/clean.cs", "tests/clean.tests.cs");

        // Mixed task: manifest includes llm-tasks/ entry (should be dropped)
        var mixedRunner = new BadManifestSubagentRunner();

        var config = new RelayConfig(
            TasksDir: "llm-tasks",
            TestCommand: "dotnet test",
            TestFileCommand: "dotnet test {files}",
            LogSources: [],
            TierProfiles: new Dictionary<string, string>(),
            MaxVerifyLoops: 5,
            MaxStageFailures: 3,
            MaxTurns: 200,
            BaselineVerify: true,
            ArchiveOnDone: true,
            SubagentTimeoutMilliseconds: 1_200_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>(),
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2,
            BypassSandbox: false,
            MaxPlanConcurrency: 2,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);

        // Plan both in parallel.
        var planResults = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("clean", cleanRunner), ("mixed", mixedRunner)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None);
        Assert.Equal(2, planResults.Count);

        // Execute both serially.
        var execOptions = new RelayDriverOptions(CreateGitCommit: true, Resume: true);

        // Execute mixed first (its manifest should have llm-tasks/ entries dropped).
        var mixedDriver = new RelayDriver(
            RelayDriverDependencies.ForTests(mixedRunner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            execOptions);
        var mixedOutcome = await mixedDriver.RunTaskAsync(repo.Root, "mixed");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, mixedOutcome.Status);

        // Verify mixed manifest was cleaned.
        var mixedManifest = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "mixed", "manifest.txt"));
        Assert.DoesNotContain("llm-tasks/", mixedManifest, StringComparison.Ordinal);
        Assert.Contains("src/real.cs", mixedManifest, StringComparison.Ordinal);

        // Now execute clean.
        var cleanDriver = new RelayDriver(
            RelayDriverDependencies.ForTests(cleanRunner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            execOptions);
        var cleanOutcome = await cleanDriver.RunTaskAsync(repo.Root, "clean");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, cleanOutcome.Status);

        // Clean's commit must NOT contain mixed's files.
        var cleanCommitFiles = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.DoesNotContain("src/real.cs", cleanCommitFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("llm-tasks/extra.md", cleanCommitFiles, StringComparison.Ordinal);
    }
}
