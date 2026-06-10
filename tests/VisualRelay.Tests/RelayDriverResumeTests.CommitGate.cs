using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverResumeTests
{
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
