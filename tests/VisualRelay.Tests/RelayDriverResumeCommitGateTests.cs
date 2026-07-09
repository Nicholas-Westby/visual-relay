using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverResumeCommitGateTests
{
    // ── commit-gate resume tests (e–f) ─────────────────────────────────

    [Fact]
    public async Task RunTaskAsync_Resume_CommitGateWithMatchingHash_SkipsToCommit()
    {
        // Scenario: stages 1–11 Done, stage 12 Flagged.
        // The tree hash in the stage-11 seal matches the current worktree.
        // Expect: re-validation passes, only stage 12 runs (driver stage, no
        // subagent invocation), outcome Committed.
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", []);
        repo.WriteTask("commit-resume", "# Commit resume\n");

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "hello");

        var manifest = new[] { "src/app.cs" };
        var treeHash = RelayDriverResumeTestHelpers.ComputeTreeHash(repo.Root, manifest);
        RelayDriverResumeTestHelpers.SetupCommitGateResumeScenario(repo.Root, "commit-resume", manifest, treeHash);

        // Gate re-validation must call the test runner.  Use a recording
        // runner so we can assert it was actually invoked — without the
        // implementation the driver skips straight to stage 12 and never
        // touches the test runner on a commit-gate resume.
        var recordingTestRunner = new RecordingTestRunner(
            new TestRunResult(0, "green"));
        // Subagent guard: throws if any LLM stage is invoked (stages 1–11 must
        // be skipped on a successful commit-gate resume).
        var guardRunner = new CommitGateGuardSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, guardRunner, recordingTestRunner,
                new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "commit-resume");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // Re-validation must have called the test runner for the gate re-run.
        Assert.NotEmpty(recordingTestRunner.Calls);
        Assert.False(guardRunner.WasCalled,
            "stages 1–11 should not re-execute on commit-gate resume when hash matches");
    }

    [Fact]
    public async Task RunTaskAsync_Resume_CommitGateWithHashMismatch_RestartsFromStage5()
    {
        // Scenario: stages 1–11 Done, stage 12 Flagged, but the worktree has
        // been modified so the tree hash no longer matches the stage-11 seal.
        // Expect: re-validation fails → driver restarts at stage 5 and runs
        // through stage 12; stages 1–4 must NOT be re-executed.
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", []);
        repo.WriteTask("dirty-resume", "# Dirty resume\n");

        // Minimal git repo so the stage-5 worktree filter can enumerate.
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "original content");
        sim.Commit(repo.Root, "seed");

        var manifest = new[] { "src/app.cs" };
        var originalTreeHash = RelayDriverResumeTestHelpers.ComputeTreeHash(repo.Root, manifest);
        RelayDriverResumeTestHelpers.SetupCommitGateResumeScenario(repo.Root, "dirty-resume", manifest, originalTreeHash);

        // Modify the file so the tree hash no longer matches.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "modified by hand");

        // Test runner: first call is commit-gate re-validation (green — but
        // hash mismatch still triggers fallback), second is stage 5 author
        // gate (must be red), third is stage 10 verify (green).
        var capturingRunner = new CapturingSubagentRunner();
        capturingRunner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(capturingRunner,
                new ScriptedTestRunner(
                    new TestRunResult(0, "green"),   // re-validation gate (ignored — hash mismatch)
                    new TestRunResult(1, "red"),     // stage 5 author gate
                    new TestRunResult(0, "green")),  // stage 10 verify
                new InMemoryRelayEventSink(),
                sim),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "dirty-resume");

        // Should complete (not flag) after re-executing from stage 5.
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Stage 5 must have been invoked (the restart point).
        Assert.Contains(capturingRunner.Invocations, inv => inv.Stage.Number == 5);

        // Stages 1–4 must NOT be invoked. (Stage 0 is the visual-triage
        // probe launched by the Review pair — it is not a pipeline stage.)
        Assert.DoesNotContain(capturingRunner.Invocations, inv => inv.Stage.Number is > 0 and <= 4);
    }

    /// <summary>
    /// A subagent runner that asserts no LLM stage is invoked.
    /// On a successful commit-gate resume only stage 12 (a driver stage) runs;
    /// the subagent runner is never called.
    /// </summary>
    private sealed class CommitGateGuardSubagentRunner : ISubagentRunner
    {
        public bool WasCalled { get; private set; }

        public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            Assert.Fail(
                $"Subagent runner was called for stage {invocation.Stage.Number} " +
                $"({invocation.Stage.Name}) on commit-gate resume. " +
                "Expected only stage 12 (Commit, a driver stage) to run.");
            throw new InvalidOperationException("unreachable");
        }
    }
}
