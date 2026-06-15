using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayQueueControllerDrainTests
{
    /// <summary>
    /// When the planning phase produces a Flagged outcome, the drain must
    /// continue without throwing.  The task must be recorded as flagged,
    /// the NEEDS-REVIEW marker written, and subsequent tasks must still
    /// be processed.
    ///
    /// Although <see cref="RelayQueueController.WriteNeedsReviewMarker"/> is
    /// private and the class is sealed, this integration test exercises the
    /// full planning-phase flag path — proving the drain does not abort when
    /// a task flags during planning.
    /// </summary>
    [Fact]
    public async Task DrainAsync_PlanningPhase_FlaggedTask_ContinuesDrainAndWritesMarker()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha — will flag in planning\n");
        repo.WriteTask("beta", "# Beta — must still run\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        // Alpha flags at stage 3 (Diagnose).  Beta runs normally.
        var flagAt3 = new FlagAtStageSubagentRunner(flagAtStage: 3);
        var betaRunner = new ScriptedSubagentRunner();
        betaRunner.SeedHappyPath("src/beta.cs", "tests/beta.tests.cs");

        var controller = new RelayQueueController(
            repo.Root,
            new RecordingTaskRunner(),
            planSubagentRunnerFactory: taskId => taskId == "alpha" ? flagAt3 : betaRunner,
            planTestRunner: new ScriptedTestRunner());

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Alpha must be flagged — the drain must not have thrown.
        var alphaResult = results.SingleOrDefault(r => r.TaskId == "alpha");
        Assert.NotNull(alphaResult);
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, alphaResult!.Status);

        // NEEDS-REVIEW marker must exist (written by WriteNeedsReviewMarker).
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "alpha", "NEEDS-REVIEW")));

        // Alpha must be set aside for review in the queue.
        Assert.Contains(controller.Tasks, t => t.Id == "alpha" && t.NeedsReview);

        // Beta must have run (drain continued past alpha into Phase 2).
        var betaResult = results.SingleOrDefault(r => r.TaskId == "beta");
        Assert.NotNull(betaResult);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, betaResult!.Status);

        // Drain completed normally (not Failed, not halted mid-way).
        Assert.NotEqual(RelayQueueState.Failed, controller.State);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));
    }

    /// <summary>
    /// When <c>CopyArtifactsBack</c> throws during planning (e.g. because
    /// <c>.relay/{taskId}</c> is a file, not a directory), the drain must
    /// continue.  <see cref="PlanPhaseRunner.PlanOneAsync"/> catches the
    /// exception and records a Failed outcome; the queue controller then
    /// continues to the next task.
    ///
    /// We trigger this by creating <c>.relay/alpha</c> as a file before the
    /// drain so that <c>CopyArtifactsBack</c> cannot create the directory.
    /// </summary>
    [Fact]
    public async Task DrainAsync_PlanningPhase_CopyArtifactsBackIOException_ContinuesDrain()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha — planning artifacts fail to copy\n");
        repo.WriteTask("beta", "# Beta — must still run\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        // Create .relay/alpha as a FILE so CopyArtifactsBack throws.
        var relayDir = Path.Combine(repo.Root, ".relay");
        Directory.CreateDirectory(relayDir);
        File.WriteAllText(Path.Combine(relayDir, "alpha"), "trap");

        var flagAt3 = new FlagAtStageSubagentRunner(flagAtStage: 3);
        var betaRunner = new ScriptedSubagentRunner();
        betaRunner.SeedHappyPath("src/beta.cs", "tests/beta.tests.cs");

        var controller = new RelayQueueController(
            repo.Root,
            new RecordingTaskRunner(),
            planSubagentRunnerFactory: taskId => taskId == "alpha" ? flagAt3 : betaRunner,
            planTestRunner: new ScriptedTestRunner());

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Alpha must have a result — the drain must not have thrown.
        var alphaResult = results.SingleOrDefault(r => r.TaskId == "alpha");
        Assert.NotNull(alphaResult);
        // CopyArtifactsBack failure causes PlanOneAsync to record Failed.
        Assert.True(
            alphaResult!.Status is RelayTaskOutcomeStatus.Flagged or RelayTaskOutcomeStatus.Failed,
            $"expected Flagged or Failed, got {alphaResult.Status}: {alphaResult.Reason}");

        // Beta must still be attempted.
        var betaResult = results.SingleOrDefault(r => r.TaskId == "beta");
        Assert.NotNull(betaResult);

        // The drain must not be in a failed state.
        Assert.NotEqual(RelayQueueState.Failed, controller.State);
    }

    /// <summary>
    /// A subagent runner that flags at a given stage AND creates
    /// <c>.relay/{taskId}/NEEDS-REVIEW</c> as a <em>directory</em> in the
    /// worktree.  <c>CopyArtifactsBack</c> faithfully reproduces this
    /// directory in the main repo, so when <c>WriteNeedsReviewMarker</c>
    /// later calls <c>File.WriteAllText</c> on the same path it throws
    /// because NEEDS-REVIEW is a directory, not a file.
    /// </summary>
    private sealed class NeedsReviewDirPoisoningRunner : ISubagentRunner
    {
        private readonly ScriptedSubagentRunner _inner = new();
        private readonly int _flagAtStage;

        public NeedsReviewDirPoisoningRunner(int flagAtStage)
        {
            _flagAtStage = flagAtStage;
        }

        public void SeedHappyPath(string codeFile, string testFile) =>
            _inner.SeedHappyPath(codeFile, testFile);

        public async Task<SubagentResult> RunAsync(StageInvocation inv, CancellationToken ct = default)
        {
            if (inv.Stage.Number < _flagAtStage)
            {
                return await _inner.RunAsync(inv, ct);
            }

            // Create NEEDS-REVIEW as a directory in the worktree artifact dir.
            // CopyArtifactsBack will copy it to the main repo as a directory.
            // WriteNeedsReviewMarker's File.WriteAllText will then throw
            // because the path is a directory, not a writable file.
            var needsReviewPath = Path.Combine(
                inv.TargetRoot, ".relay", inv.TaskName, "NEEDS-REVIEW");
            Directory.CreateDirectory(needsReviewPath);

            return new SubagentResult(
                string.Empty, null, false,
                $"synthetic flag at stage {_flagAtStage}");
        }
    }

    /// <summary>
    /// When <see cref="RelayQueueController.WriteNeedsReviewMarker"/> throws
    /// an <c>IOException</c> during the planning phase (e.g. because
    /// <c>.relay/{taskId}/NEEDS-REVIEW</c> is a directory, not a file), the
    /// drain must continue.  The phase-2 guard (line 251) already wraps the
    /// call; this test proves the phase-1 guard added in Bug 3 works.
    /// </summary>
    [Fact]
    public async Task DrainAsync_PlanningPhase_WriteNeedsReviewMarkerIOException_ContinuesDrain()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha — WriteNeedsReviewMarker will throw\n");
        repo.WriteTask("beta", "# Beta — must still run\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        // Alpha's subagent flags at stage 3 AND creates NEEDS-REVIEW as a
        // directory in the worktree artifact dir.  After CopyArtifactsBack,
        // the main repo's .relay/alpha/NEEDS-REVIEW is a directory, so
        // WriteNeedsReviewMarker's File.WriteAllText throws.
        var poisoner = new NeedsReviewDirPoisoningRunner(flagAtStage: 3);
        poisoner.SeedHappyPath("src/alpha.cs", "tests/alpha.tests.cs");
        var betaRunner = new ScriptedSubagentRunner();
        betaRunner.SeedHappyPath("src/beta.cs", "tests/beta.tests.cs");

        var controller = new RelayQueueController(
            repo.Root,
            new RecordingTaskRunner(),
            planSubagentRunnerFactory: taskId => taskId == "alpha" ? poisoner : betaRunner,
            planTestRunner: new ScriptedTestRunner());

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Alpha must be flagged — the drain must not have thrown.
        var alphaResult = results.SingleOrDefault(r => r.TaskId == "alpha");
        Assert.NotNull(alphaResult);
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, alphaResult!.Status);

        // Beta must still be attempted (drain continued past alpha).
        var betaResult = results.SingleOrDefault(r => r.TaskId == "beta");
        Assert.NotNull(betaResult);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, betaResult!.Status);

        // Drain must not be in a failed state.
        Assert.NotEqual(RelayQueueState.Failed, controller.State);
    }
}
