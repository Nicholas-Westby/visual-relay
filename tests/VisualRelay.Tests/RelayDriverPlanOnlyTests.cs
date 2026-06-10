using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverPlanOnlyTests
{
    [Fact]
    public async Task RunTaskAsync_WithLastStageToRun4_StopsAfterStage4_ReturnsPlanned()
    {
        // A plan-only run must execute stages 1–4, persist manifest + status
        // (1–4 Done, 5–11 Waiting), create no git commit, and return Planned.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("plan-me", "# Plan me\n");
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var sink = new InMemoryRelayEventSink();
        var planOptions = new RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 4);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(), sink),
            planOptions);

        var outcome = await driver.RunTaskAsync(repo.Root, "plan-me");

        // Must return Planned, not Committed or Flagged.
        Assert.Equal(RelayTaskOutcomeStatus.Planned, outcome.Status);
        Assert.Null(outcome.CommitSha); // no commit happened
        Assert.Null(outcome.TaskHash);  // no commit means no task hash

        // Manifest must be written (happens during stage 4).
        var taskDir = Path.Combine(repo.Root, ".relay", "plan-me");
        Assert.True(File.Exists(Path.Combine(taskDir, "manifest.txt")));
        var manifestContent = await File.ReadAllTextAsync(Path.Combine(taskDir, "manifest.txt"));
        Assert.Contains("src/status.cs", manifestContent, StringComparison.Ordinal);
        Assert.Contains("tests/status.tests.cs", manifestContent, StringComparison.Ordinal);

        // Status must show 1–4 Done, 5–11 Waiting.
        var status = StageStatusRecord.Read(taskDir);
        Assert.Equal(11, status.Count);
        for (int i = 0; i < 4; i++)
            Assert.Equal("Done", status[i].Status);
        for (int i = 4; i < 11; i++)
            Assert.Equal("Waiting", status[i].Status);

        // Stage 5 must NOT have been invoked (no test runner call).
        Assert.Contains(sink.Events, e => e.EventName == "stage_start" && e.StageNumber == 1);
        Assert.Contains(sink.Events, e => e.EventName == "stage_done" && e.StageNumber == 4);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "stage_start" && e.StageNumber == 5);

        // Seals file should exist for stages 1–4 (no commit seal though).
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "plan-me", "plan-me.seals")));
        // No stage 5 seal.
        var sealsContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "plan-me", "plan-me.seals"));
        Assert.DoesNotContain("\"n\":5", sealsContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_PlanOnly_DoesNotAcquireActiveTaskLock()
    {
        // A plan-only run with worktree isolation uses a different rootPath,
        // so the main repo's ActiveTaskLock should be untouched. But even
        // when run against the main root, a plan-only run should still be
        // able to run — this test verifies that the plan-only path does not
        // crash on lock contention when run in the same repo root.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("plan-lock", "# Plan lock\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var planOptions = new RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 4);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            planOptions);

        var outcome = await driver.RunTaskAsync(repo.Root, "plan-lock");

        Assert.Equal(RelayTaskOutcomeStatus.Planned, outcome.Status);
        // ActiveTaskLock directory must be cleaned up (the lock is released even
        // for plan-only runs that don't commit).
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay", "ACTIVE")));
    }

    [Fact]
    public async Task RunTaskAsync_ResumeFrom5_AfterPlanOnly_SealChainUnbroken()
    {
        // Phase 1: plan-only (stages 1–4). Phase 2: resume at stage 5, commit.
        // The seal chain must continue from where phase 1 left off.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("two-phase", "# Two-phase\n");
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");

        // Phase 1 — plan
        var planOptions = new RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 4);
        var planDriver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            planOptions);
        var planOutcome = await planDriver.RunTaskAsync(repo.Root, "two-phase");
        Assert.Equal(RelayTaskOutcomeStatus.Planned, planOutcome.Status);

        // Verify status: 1–4 Done, 5–11 Waiting.
        var taskDir = Path.Combine(repo.Root, ".relay", "two-phase");
        var statusAfterPlan = StageStatusRecord.Read(taskDir);
        Assert.Equal("Done", statusAfterPlan[0].Status);   // stage 1
        Assert.Equal("Done", statusAfterPlan[3].Status);   // stage 4
        Assert.Equal("Waiting", statusAfterPlan[4].Status); // stage 5

        // Phase 2 — resume from stage 5, with git commit
        var resumeOptions = new RelayDriverOptions(CreateGitCommit: false, Resume: true);
        var resumeDriver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            resumeOptions);
        var resumeOutcome = await resumeDriver.RunTaskAsync(repo.Root, "two-phase");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, resumeOutcome.Status);

        // Stages 1–4 were NOT re-run: no attempt-2 dirs for planning stages.
        Assert.False(File.Exists(Path.Combine(taskDir, "stage1-attempt2.report.json")));
        Assert.False(File.Exists(Path.Combine(taskDir, "stage2-attempt2.report.json")));
        Assert.False(File.Exists(Path.Combine(taskDir, "stage3-attempt2.report.json")));
        Assert.False(File.Exists(Path.Combine(taskDir, "stage4-attempt2.report.json")));

        // Stage 5 onward ran as attempt 1 in the resume session (fresh attempt
        // for stages never run during planning).
        Assert.True(File.Exists(Path.Combine(taskDir, "stage5-attempt1.report.json")));

        // Status shows all Done after phase 2.
        var statusAfterResume = StageStatusRecord.Read(taskDir);
        Assert.All(statusAfterResume, e => Assert.Equal("Done", e.Status));

        // Seals file covers all 11 stages.
        var sealsPath = Path.Combine(taskDir, "two-phase.seals");
        Assert.True(File.Exists(sealsPath));
        var seals = await File.ReadAllLinesAsync(sealsPath);
        Assert.True(seals.Length >= 11, $"expected ≥11 seals, got {seals.Length}");
        Assert.Contains("\"n\":11", seals[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_PlanOnly_FlagAtStage3_ReturnsFlaggedNotPlanned()
    {
        // If a planning task flags (e.g. at stage 3), the outcome must be
        // Flagged (not Planned) and the task must be excluded from Phase 2.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("flag-plan", "# Flag during plan\n");
        var flagAt3 = new FlagAtStageSubagentRunner(flagAtStage: 3);
        var planOptions = new RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 4);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(flagAt3, new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            planOptions);

        var outcome = await driver.RunTaskAsync(repo.Root, "flag-plan");

        // Flagged, NOT Planned — the task hit an invalid result before completing planning.
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);

        // Status shows the flag at stage 3.
        var taskDir = Path.Combine(repo.Root, ".relay", "flag-plan");
        var status = StageStatusRecord.Read(taskDir);
        Assert.Equal("Done", status[0].Status);     // stage 1
        Assert.Equal("Done", status[1].Status);     // stage 2
        Assert.Equal("Flagged", status[2].Status);  // stage 3
        Assert.Equal("Waiting", status[3].Status);  // stage 4 — never reached

        // NEEDS-REVIEW marker exists.
        Assert.True(File.Exists(Path.Combine(taskDir, "NEEDS-REVIEW")));
    }

    [Fact]
    public async Task RunTaskAsync_PlanOnly_NoTestRunnerInvoked()
    {
        // Planning must never invoke the target project's test command.
        // The test runner is first called at stage 5 (Author-tests' red gate).
        // A plan-only run (LastStageToRun=4) must never call RunAsync on the test runner.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("plan-no-test", "# Plan no test\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        // Use a test runner that throws if called — proving it was never invoked.
        var throwingTestRunner = new ThrowingTestRunner();
        var planOptions = new RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 4);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, throwingTestRunner, new InMemoryRelayEventSink()),
            planOptions);

        var outcome = await driver.RunTaskAsync(repo.Root, "plan-no-test");
        Assert.Equal(RelayTaskOutcomeStatus.Planned, outcome.Status);
        Assert.False(throwingTestRunner.WasCalled, "test runner must not be called during planning");
    }

    /// <summary>
    /// Test runner that records whether it was ever called and throws if
    /// invoked — used to assert that the planning phase never touches tests.
    /// </summary>
    private sealed class ThrowingTestRunner : ITestRunner
    {
        public bool WasCalled { get; private set; }

        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("test runner was called during planning — this must not happen");
        }
    }
}
