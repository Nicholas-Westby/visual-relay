using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class PlanPhaseRunnerTests
{
    [Fact]
    public async Task RunPlanPhase_EnforcesBatchLimit_NoMoreThanMaxConcurrencyInFlight()
    {
        // When maxPlanConcurrency is e.g. 3 and we have 10 tasks, the peak
        // concurrent planning runs must never exceed 3.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        const int taskCount = 10;
        const int maxConcurrency = 3;
        for (int i = 0; i < taskCount; i++)
            repo.WriteTask($"task-{i:D2}", $"# Task {i}\n");
        // Git repo required for worktree creation.
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        // Each task needs its own CountingConcurrencySubagentRunner so the
        // Interlocked counter is shared across all concurrent runs.
        var sharedCounter = new CountingConcurrencySubagentRunner();
        sharedCounter.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var tasks = Enumerable.Range(0, taskCount).Select(i => (
            TaskId: $"task-{i:D2}",
            Runner: (ISubagentRunner)sharedCounter
        )).ToList();

        // Run PlanPhaseRunner directly — it orchestrates the parallel plan phase.
        var config = PlanPhaseTestHelpers.MakeConfig(maxConcurrency);

        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: tasks,
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);

        // All tasks should complete planning.
        Assert.Equal(taskCount, results.Count);
        Assert.All(results, r => Assert.True(
            r.Outcome.Status is RelayTaskOutcomeStatus.Planned or RelayTaskOutcomeStatus.Flagged,
            $"task {r.TaskId} had unexpected status {r.Outcome.Status}"));

        // Peak concurrency must be ≤ maxConcurrency.
        Assert.True(sharedCounter.PeakConcurrency <= maxConcurrency,
            $"peak concurrency {sharedCounter.PeakConcurrency} exceeded limit {maxConcurrency}");
        // At least some concurrency must have happened (otherwise the test is broken).
        Assert.True(sharedCounter.PeakConcurrency >= 2,
            $"expected at least 2 concurrent runs, got peak {sharedCounter.PeakConcurrency}");
    }

    [Fact]
    public async Task RunPlanPhase_EachTaskGetsOwnArtifactDirectory()
    {
        // N concurrent planning tasks must each produce their own
        // .relay/<taskId>/ artifacts without writing into another task's directory.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        // Git repo required for worktree creation.
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var tasks = new[]
        {
            ("alpha", MakeRunner("src/alpha.cs", "tests/alpha.tests.cs")),
            ("beta",  MakeRunner("src/beta.cs", "tests/beta.tests.cs")),
            ("gamma", MakeRunner("src/gamma.cs", "tests/gamma.tests.cs")),
        };

        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 3);
        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: tasks,
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(RelayTaskOutcomeStatus.Planned, r.Outcome.Status));

        // Each task's .relay/<id>/ manifest must contain ONLY its own files,
        // never another task's.
        var alphaManifest = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "alpha", "manifest.txt"));
        Assert.Contains("src/alpha.cs", alphaManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("src/beta.cs", alphaManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("src/gamma.cs", alphaManifest, StringComparison.Ordinal);

        var betaManifest = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "beta", "manifest.txt"));
        Assert.Contains("src/beta.cs", betaManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("src/alpha.cs", betaManifest, StringComparison.Ordinal);

        var gammaManifest = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "gamma", "manifest.txt"));
        Assert.Contains("src/gamma.cs", gammaManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("src/beta.cs", gammaManifest, StringComparison.Ordinal);

        // Each must have its own status.json with 1–4 Done.
        foreach (var taskId in new[] { "alpha", "beta", "gamma" })
        {
            var status = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", taskId));
            Assert.Equal(11, status.Count);
            Assert.Equal("Done", status[0].Status);
            Assert.Equal("Done", status[3].Status);
            Assert.Equal("Waiting", status[4].Status);
        }
    }

    [Fact]
    public async Task RunPlanPhase_StrayShellWriteStaysInWorktree()
    {
        // A planning agent that shells a write (simulated by ShellWritingSubagentRunner)
        // must NOT modify the main repo's working tree. The write must land in
        // the ephemeral worktree that gets discarded.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // Create a tracked file in the main repo so we can detect contamination.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "clean.cs"), "pristine");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        repo.WriteTask("stray-writer", "# Stray writer\n");
        var strayWriter = new ShellWritingSubagentRunner("contamination.txt");
        strayWriter.SeedHappyPath("src/clean.cs", "tests/clean.tests.cs");

        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 1);
        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("stray-writer", strayWriter)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);

        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Planned, results[0].Outcome.Status);

        // The stray write must NOT be in the main repo tree.
        Assert.False(File.Exists(Path.Combine(repo.Root, "contamination.txt")),
            "stray shell write leaked into the main repo working tree");

        // The main repo's tracked file must still be pristine.
        var cleanContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "clean.cs"));
        Assert.Equal("pristine", cleanContent);

        // The worktree should have been cleaned up (no leftover temp dirs with
        // our contamination file).
        Assert.NotNull(strayWriter.WrittenFilePath);
        Assert.False(File.Exists(strayWriter.WrittenFilePath!),
            "worktree was not cleaned up — stray write file still exists on disk");
    }

    [Fact]
    public async Task RunPlanPhase_NeverThrowsActiveTaskLockCollision()
    {
        // Parallel planning runs each use their own worktree with their own
        // .relay/ACTIVE directory, so no run should ever throw
        // "relay: another task is already active". This test runs 5 planning
        // tasks concurrently with maxConcurrency=5 to maximize collision
        // potential.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        const int taskCount = 5;
        for (int i = 0; i < taskCount; i++)
            repo.WriteTask($"lock-{i:D2}", $"# Lock test {i}\n");
        // Git repo required for worktree creation.
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var runners = Enumerable.Range(0, taskCount)
            .Select(i =>
            {
                var runner = new ScriptedSubagentRunner();
                runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
                return ($"lock-{i:D2}", (ISubagentRunner)runner);
            })
            .ToArray();

        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: taskCount);
        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: runners,
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);

        Assert.Equal(taskCount, results.Count);
        // No task should have Failed with "another task is already active".
        Assert.All(results, r => Assert.NotEqual(RelayTaskOutcomeStatus.Failed, r.Outcome.Status));
        Assert.All(results, r => Assert.DoesNotContain("another task is already active",
            r.Outcome.Reason ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPlanPhase_CopiesArtifactsBackToMainRepo()
    {
        // After a planning task completes in its worktree, its .relay/<taskId>/
        // artifacts must be copied back to the main repo so the serial execute
        // phase can find them.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("copy-back", "# Copy back\n");
        // Git repo required for worktree creation.
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 1);
        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("copy-back", runner)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);

        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Planned, results[0].Outcome.Status);

        // Main repo must have the artifacts.
        var taskDir = Path.Combine(repo.Root, ".relay", "copy-back");
        Assert.True(Directory.Exists(taskDir));
        Assert.True(File.Exists(Path.Combine(taskDir, "manifest.txt")));
        Assert.True(File.Exists(Path.Combine(taskDir, "status.json")));
        Assert.True(File.Exists(Path.Combine(taskDir, "ledger.md")));

        // Trace directories (stageN-attemptM) must exist.
        Assert.True(Directory.Exists(Path.Combine(taskDir, "stage1-attempt1")));
        Assert.True(File.Exists(Path.Combine(taskDir, "stage1-attempt1.report.json")));
    }

    [Fact]
    public async Task RunPlanPhase_FlaggedTasksAreReturnedButExcludedFromExecutePhase()
    {
        // Tasks that flag during planning must be returned with Flagged status
        // and must NOT proceed to the execute phase. The plan runner must
        // still copy back the NEEDS-REVIEW marker and partial artifacts.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("flag-in-plan", "# Flag in plan\n");
        // Git repo required for worktree creation.
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);
        var flagAt3 = new FlagAtStageSubagentRunner(flagAtStage: 3);

        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 1);
        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("flag-in-plan", flagAt3)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);

        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, results[0].Outcome.Status);

        // NEEDS-REVIEW marker must be copied back to the main repo.
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "flag-in-plan", "NEEDS-REVIEW")));

        // Partial status must exist (stages 1–2 Done, 3 Flagged).
        var status = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "flag-in-plan"));
        Assert.Equal("Done", status[0].Status);
        Assert.Equal("Done", status[1].Status);
        Assert.Equal("Flagged", status[2].Status);
    }

    private static ISubagentRunner MakeRunner(string codeFile, string testFile)
    {
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath(codeFile, testFile);
        return runner;
    }
}
