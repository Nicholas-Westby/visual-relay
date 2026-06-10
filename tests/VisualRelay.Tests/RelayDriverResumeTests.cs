using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverResumeTests
{
    [Fact]
    public async Task RunTaskAsync_Resume_SkipsDoneStagesAndContinuesFromFlaggedStage()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("resume-me", "# Resume me\n");

        // --- Run 1: flag at stage 3 (Diagnose) ---
        var flagAt3 = new FlagAtStageSubagentRunner(flagAtStage: 3);
        var driver1 = new RelayDriver(
            RelayDriverDependencies.ForTests(flagAt3, new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome1 = await driver1.RunTaskAsync(repo.Root, "resume-me");

        // Should have flagged at stage 3.
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome1.Status);
        var taskDir = Path.Combine(repo.Root, ".relay", "resume-me");

        // status.json must show stages 1-2 Done, stage 3 Flagged, 4-11 Waiting.
        var statusAfterRun1 = StageStatusRecord.Read(taskDir);
        Assert.NotEmpty(statusAfterRun1);
        Assert.Equal("Done", statusAfterRun1[0].Status);   // stage 1
        Assert.Equal("Done", statusAfterRun1[1].Status);   // stage 2
        Assert.Equal("Flagged", statusAfterRun1[2].Status); // stage 3
        Assert.All(statusAfterRun1.Skip(3), e => Assert.Equal("Waiting", e.Status));

        // seals file has 2 entries (stages 1-2).
        var sealsPath = Path.Combine(taskDir, "resume-me.seals");
        Assert.True(File.Exists(sealsPath));
        var sealsRun1 = await File.ReadAllLinesAsync(sealsPath);
        Assert.Equal(2, sealsRun1.Length);

        // ledger has sections for stages 1-2.
        var ledgerPath = Path.Combine(taskDir, "ledger.md");
        Assert.True(File.Exists(ledgerPath));
        var ledgerRun1 = await File.ReadAllTextAsync(ledgerPath);
        Assert.Contains("Stage 1 - Ideate", ledgerRun1, StringComparison.Ordinal);
        Assert.Contains("Stage 2 - Research", ledgerRun1, StringComparison.Ordinal);
        Assert.DoesNotContain("Stage 3 - Diagnose", ledgerRun1, StringComparison.Ordinal);

        // NEEDS-REVIEW exists.
        Assert.True(File.Exists(Path.Combine(taskDir, "NEEDS-REVIEW")));

        // --- Run 2: resume from stage 3 with a happy-path runner ---
        var happyRunner = new ArtifactWritingSubagentRunner();
        happyRunner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(happyRunner, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome2 = await driver2.RunTaskAsync(repo.Root, "resume-me");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);

        // Stages 1-2 were skipped: no attempt-2 dirs or reports.
        Assert.False(File.Exists(Path.Combine(taskDir, "stage1-attempt2.report.json")));
        Assert.False(Directory.Exists(Path.Combine(taskDir, "stage1-attempt2")));
        Assert.False(File.Exists(Path.Combine(taskDir, "stage2-attempt2.report.json")));
        Assert.False(Directory.Exists(Path.Combine(taskDir, "stage2-attempt2")));

        // Stage 3 onward re-ran: attempt-2 report exists.
        Assert.True(File.Exists(Path.Combine(taskDir, "stage3-attempt2.report.json")));

        // Seals file extended: ≥ 11 entries (2 from run 1 + 9 from run 2).
        var sealsRun2 = await File.ReadAllLinesAsync(sealsPath);
        Assert.True(sealsRun2.Length >= 11, $"expected ≥ 11 seal entries, got {sealsRun2.Length}");
        Assert.Contains("\"n\":11", sealsRun2[^1], StringComparison.Ordinal);

        // Ledger contains all 11 stage sections.
        var ledgerRun2 = await File.ReadAllTextAsync(ledgerPath);
        for (int n = 1; n <= 11; n++)
        {
            Assert.Contains($"Stage {n} -", ledgerRun2, StringComparison.Ordinal);
        }

        // Status shows all stages Done.
        var statusAfterRun2 = StageStatusRecord.Read(taskDir);
        Assert.All(statusAfterRun2, e => Assert.Equal("Done", e.Status));

        // NEEDS-REVIEW is gone.
        Assert.False(File.Exists(Path.Combine(taskDir, "NEEDS-REVIEW")));
    }

    [Fact]
    public async Task RunTaskAsync_Resume_NoPriorRun_BehavesLikeFreshRun()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("first-time", "# First time\n");

        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "first-time");

        // Should complete normally from stage 1.
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var taskDir = Path.Combine(repo.Root, ".relay", "first-time");

        // All stage-1 trace artifacts should exist (attempt 1, not 2).
        Assert.True(File.Exists(Path.Combine(taskDir, "stage1-attempt1.report.json")));
        Assert.True(Directory.Exists(Path.Combine(taskDir, "stage1-attempt1")));

        // Seals file has all 11 entries starting from n=1.
        var sealsPath = Path.Combine(taskDir, "first-time.seals");
        Assert.True(File.Exists(sealsPath));
        var seals = await File.ReadAllLinesAsync(sealsPath);
        Assert.True(seals.Length >= 11);
        Assert.Contains("\"n\":1", seals[0], StringComparison.Ordinal);
        Assert.Contains("\"n\":11", seals[^1], StringComparison.Ordinal);

        // Status shows all Done.
        var status = StageStatusRecord.Read(taskDir);
        Assert.All(status, e => Assert.Equal("Done", e.Status));
    }

    [Fact]
    public async Task RunTaskAsync_NormalRerun_StartsFromStage1()
    {
        // A normal re-run (no Resume flag) must behave exactly as today:
        // every stage runs fresh from attempt 1, even when a prior run exists.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("rerun-clean", "# Rerun clean\n");

        // Run 1: complete a full run.
        await RunHappyPath(repo, "rerun-clean");

        // Run 2: another full run WITHOUT resume.
        await RunHappyPath(repo, "rerun-clean");

        var taskDir = Path.Combine(repo.Root, ".relay", "rerun-clean");

        // Both attempts exist for stage 1 (fresh re-run, not skipped).
        Assert.True(File.Exists(Path.Combine(taskDir, "stage1-attempt1.report.json")));
        Assert.True(File.Exists(Path.Combine(taskDir, "stage1-attempt2.report.json")));
        Assert.True(Directory.Exists(Path.Combine(taskDir, "stage1-attempt1")));
        Assert.True(Directory.Exists(Path.Combine(taskDir, "stage1-attempt2")));

        // Seals file has 11 entries from the second run (normal re-runs overwrite,
        // unlike resume runs which extend the chain).
        var sealsPath = Path.Combine(taskDir, "rerun-clean.seals");
        var seals = await File.ReadAllLinesAsync(sealsPath);
        Assert.Equal(11, seals.Length);
    }

    private static async Task RunHappyPath(TestRepository repo, string taskId)
    {
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        Assert.Equal(RelayTaskOutcomeStatus.Committed,
            (await driver.RunTaskAsync(repo.Root, taskId)).Status);
    }

    // ── commit-gate resume tests (e–f) ─────────────────────────────────

    [Fact]
    public async Task RunTaskAsync_Resume_CommitGateWithMatchingHash_SkipsToCommit()
    {
        // Scenario: stages 1–10 Done, stage 11 Flagged.
        // The tree hash in the stage-10 seal matches the current worktree.
        // Expect: re-validation passes, only stage 11 runs (driver stage, no
        // subagent invocation), outcome Committed.
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", []);
        repo.WriteTask("commit-resume", "# Commit resume\n");

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "hello");

        var taskDir = Path.Combine(repo.Root, ".relay", "commit-resume");
        var manifest = new[] { "src/app.cs" };
        var treeHash = ComputeTreeHash(repo.Root, manifest);
        SetupCommitGateResumeScenario(repo.Root, "commit-resume", manifest, treeHash);

        // Gate re-validation must call the test runner.  Use a recording
        // runner so we can assert it was actually invoked — without the
        // implementation the driver skips straight to stage 11 and never
        // touches the test runner on a commit-gate resume.
        var recordingTestRunner = new RecordingTestRunner(
            new TestRunResult(0, "green"));
        // Subagent guard: throws if any LLM stage is invoked (stages 1–10 must
        // be skipped on a successful commit-gate resume).
        var guardRunner = new CommitGateGuardSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(guardRunner, recordingTestRunner,
                new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "commit-resume");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // Re-validation must have called the test runner for the gate re-run.
        Assert.NotEmpty(recordingTestRunner.Calls);
        Assert.False(guardRunner.WasCalled,
            "stages 1–10 should not re-execute on commit-gate resume when hash matches");
    }

    [Fact]
    public async Task RunTaskAsync_Resume_CommitGateWithHashMismatch_RestartsFromStage5()
    {
        // Scenario: stages 1–10 Done, stage 11 Flagged, but the worktree has
        // been modified so the tree hash no longer matches the stage-10 seal.
        // Expect: re-validation fails → driver restarts at stage 5 and runs
        // through stage 11; stages 1–4 must NOT be re-executed.
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", []);
        repo.WriteTask("dirty-resume", "# Dirty resume\n");

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "original content");

        var taskDir = Path.Combine(repo.Root, ".relay", "dirty-resume");
        var manifest = new[] { "src/app.cs" };
        var originalTreeHash = ComputeTreeHash(repo.Root, manifest);
        SetupCommitGateResumeScenario(repo.Root, "dirty-resume", manifest, originalTreeHash);

        // Modify the file so the tree hash no longer matches.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "modified by hand");

        // Test runner: first call is commit-gate re-validation (green — but
        // hash mismatch still triggers fallback), second is stage 5 author
        // gate (must be red), third is stage 9 verify (green).
        var capturingRunner = new CapturingSubagentRunner();
        capturingRunner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(capturingRunner,
                new ScriptedTestRunner(
                    new TestRunResult(0, "green"),   // re-validation gate (ignored — hash mismatch)
                    new TestRunResult(1, "red"),     // stage 5 author gate
                    new TestRunResult(0, "green")),  // stage 9 verify
                new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "dirty-resume");

        // Should complete (not flag) after re-executing from stage 5.
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Stage 5 must have been invoked (the restart point).
        Assert.Contains(capturingRunner.Invocations, inv => inv.Stage.Number == 5);

        // Stages 1–4 must NOT be invoked.
        Assert.DoesNotContain(capturingRunner.Invocations, inv => inv.Stage.Number <= 4);
    }

    // ── commit-gate resume helpers ─────────────────────────────────────

    /// <summary>
    /// Sets up a task directory with status.json (stages 1–10 Done, stage 11
    /// Flagged), a seal chain of 10 entries, manifest.txt, ledger.md, and a
    /// NEEDS-REVIEW marker — exactly what a prior run leaves behind when it
    /// flags at the commit gate.
    /// </summary>
    private static void SetupCommitGateResumeScenario(
        string repoRoot,
        string taskId,
        string[] manifest,
        string matchingTreeHash)
    {
        var taskDir = Path.Combine(repoRoot, ".relay", taskId);
        Directory.CreateDirectory(taskDir);

        // ── status.json ────────────────────────────────────────────────
        var statusEntries = new List<StageStatusEntry>(11);
        foreach (var stage in RelayStages.All)
        {
            if (stage.Number <= 10)
            {
                statusEntries.Add(new StageStatusEntry(
                    stage.Number, stage.Name, "Done",
                    Check: "green", CostUsd: 0, DurationSeconds: 1));
            }
            else
            {
                statusEntries.Add(new StageStatusEntry(
                    stage.Number, stage.Name, "Flagged",
                    Error: "target root is not a git repository"));
            }
        }

        File.WriteAllText(
            Path.Combine(taskDir, "status.json"),
            System.Text.Json.JsonSerializer.Serialize(statusEntries,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));

        // ── seals ──────────────────────────────────────────────────────
        var seals = new List<string>();
        var previousSeal = string.Empty;
        for (int n = 1; n <= 10; n++)
        {
            var stage = RelayStages.All[n - 1];
            var artifactHash = Hashing.Sha256Hex(
                n.ToString(), stage.Name, $"body for stage {n}");
            // tree hash only from stage 4 onward (matches driver behavior).
            var treeHash = n >= 4 ? matchingTreeHash : string.Empty;
            var seal = Hashing.Sha256Hex(
                previousSeal, n.ToString(),
                DateTimeOffset.UtcNow.ToString("O"),
                artifactHash, treeHash, "green");
            previousSeal = seal;

            var json = new Dictionary<string, object?>
            {
                ["kind"] = "stage",
                ["n"] = n,
                ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
                ["artifactHash"] = artifactHash,
                ["treeHash"] = treeHash,
                ["seal"] = seal,
                ["check"] = "green"
            };
            seals.Add(System.Text.Json.JsonSerializer.Serialize(json));
        }

        File.WriteAllText(
            Path.Combine(taskDir, $"{taskId}.seals"),
            string.Join(Environment.NewLine, seals) + Environment.NewLine);

        // ── manifest.txt ───────────────────────────────────────────────
        File.WriteAllText(
            Path.Combine(taskDir, "manifest.txt"),
            string.Join(Environment.NewLine, manifest) + Environment.NewLine);

        // ── ledger.md ──────────────────────────────────────────────────
        var ledger = new System.Text.StringBuilder();
        for (int n = 1; n <= 10; n++)
        {
            var stage = RelayStages.All[n - 1];
            ledger.AppendLine($"## Stage {n} - {stage.Name}");
            ledger.AppendLine();
            ledger.AppendLine($"body for stage {n}");
            ledger.AppendLine();
        }

        File.WriteAllText(Path.Combine(taskDir, "ledger.md"), ledger.ToString());

        // ── NEEDS-REVIEW ───────────────────────────────────────────────
        File.WriteAllText(Path.Combine(taskDir, "NEEDS-REVIEW"), string.Empty);
    }

    /// <summary>
    /// Replicates <c>RelayDriver.WorkingTreeHash</c> so tests can compute the
    /// expected tree hash for a given manifest. Uses <see cref="Hashing.Sha256Hex"/>
    /// which is accessible via <c>InternalsVisibleTo</c>.
    /// </summary>
    private static string ComputeTreeHash(string rootPath, string[] manifest)
    {
        var parts = new List<string>();
        foreach (var relative in manifest.Order(StringComparer.Ordinal))
        {
            var fullPath = Path.Combine(rootPath, relative);
            parts.Add(relative);
            parts.Add(File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty);
        }

        return Hashing.Sha256Hex(parts.ToArray());
    }

    /// <summary>
    /// A subagent runner that asserts no LLM stage is invoked.
    /// On a successful commit-gate resume only stage 11 (a driver stage) runs;
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
                "Expected only stage 11 (Commit, a driver stage) to run.");
            throw new InvalidOperationException("unreachable");
        }
    }
}
