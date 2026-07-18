using VisualRelay.Core.Execution;
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
            RelayDriverTestHelpers.DepsFor(repo, flagAt3, new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome1 = await driver1.RunTaskAsync(repo.Root, "resume-me");

        // Should have flagged at stage 3.
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome1.Status);
        var taskDir = Path.Combine(repo.Root, ".relay", "resume-me");

        // status.json must show stages 1-2 Done, stage 3 Flagged, 4-12 Waiting.
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
            RelayDriverTestHelpers.DepsFor(repo, happyRunner, new ScriptedTestRunner(
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

        // Seals file extended: ≥ 12 entries (2 from run 1 + 10 from run 2).
        var sealsRun2 = await File.ReadAllLinesAsync(sealsPath);
        Assert.True(sealsRun2.Length >= 12, $"expected ≥ 12 seal entries, got {sealsRun2.Length}");
        Assert.Contains("\"n\":12", sealsRun2[^1], StringComparison.Ordinal);

        // Ledger contains all 12 stage sections.
        var ledgerRun2 = await File.ReadAllTextAsync(ledgerPath);
        for (int n = 1; n <= 12; n++)
        {
            Assert.Contains($"Stage {n} -", ledgerRun2, StringComparison.Ordinal);
        }

        // Status shows all stages Done.
        var statusAfterRun2 = StageStatusRecord.Read(taskDir);
        RelayDriverTestHelpers.AssertHappyPathStatuses(statusAfterRun2);

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
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(
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

        // Seals file has all 12 entries starting from n=1.
        var sealsPath = Path.Combine(taskDir, "first-time.seals");
        Assert.True(File.Exists(sealsPath));
        var seals = await File.ReadAllLinesAsync(sealsPath);
        Assert.True(seals.Length >= 12);
        Assert.Contains("\"n\":1", seals[0], StringComparison.Ordinal);
        Assert.Contains("\"n\":12", seals[^1], StringComparison.Ordinal);

        // Status shows all Done.
        var status = StageStatusRecord.Read(taskDir);
        RelayDriverTestHelpers.AssertHappyPathStatuses(status);
    }

    [Fact]
    public async Task RunTaskAsync_NormalRerun_StartsFromStage1()
    {
        // A normal re-run (no Resume flag) must behave exactly as today:
        // every stage runs fresh from attempt 1, even when a prior run exists.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("rerun-clean", "# Rerun clean\n");

        var sim = RelayDriverTestHelpers.InitTestRepo(repo);

        // Run 1: complete a full run.
        await RelayDriverTestHelpers.RunHappyPath(repo, sim, "rerun-clean");

        // Run 2: another full run WITHOUT resume.
        await RelayDriverTestHelpers.RunHappyPath(repo, sim, "rerun-clean");

        var taskDir = Path.Combine(repo.Root, ".relay", "rerun-clean");

        // Both attempts exist for stage 1 (fresh re-run, not skipped).
        Assert.True(File.Exists(Path.Combine(taskDir, "stage1-attempt1.report.json")));
        Assert.True(File.Exists(Path.Combine(taskDir, "stage1-attempt2.report.json")));
        Assert.True(Directory.Exists(Path.Combine(taskDir, "stage1-attempt1")));
        Assert.True(Directory.Exists(Path.Combine(taskDir, "stage1-attempt2")));

        // Seals file has 12 entries from the second run (normal re-runs overwrite,
        // unlike resume runs which extend the chain).
        var sealsPath = Path.Combine(taskDir, "rerun-clean.seals");
        var seals = await File.ReadAllLinesAsync(sealsPath);
        Assert.Equal(12, seals.Length);
    }

    // ═══════════════════════════════════════════════════════════════
    // Resume-mode first run captures untracked baseline
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunTaskAsync_ResumeFirstRun_CapturesUntrackedBaseline()
    {
        // When a never-run task executes with Resume:true (the default for
        // GUI-driven runs), CapturePreRunUntrackedAsync must capture + persist
        // the current untracked state rather than fabricating an empty baseline.
        // An empty baseline misattributes every pre-existing untracked file as
        // "authored by this run", causing false commit-gate flags and
        // data-loss-hazard resets.
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", [], enableFixVerify: false);
        repo.WriteTask("capture-baseline", "# Capture baseline task\n");
        var sim = RelayDriverTestHelpers.InitTestRepo(repo);

        // Create a pre-existing untracked file — this must appear in the snapshot.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "scratch.log"), "pre-existing");

        var runner = new FileWritingSubagentRunner(
            new ScriptedSubagentRunner(), stage: 6, "src/app.cs", "implemented");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(),
                sim),
            new RelayDriverOptions(CreateGitCommit: true, Resume: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "capture-baseline");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // pre-run-untracked.txt must be written.
        var snapshotPath = Path.Combine(repo.Root, ".relay", "capture-baseline", "pre-run-untracked.txt");
        Assert.True(File.Exists(snapshotPath), "pre-run-untracked.txt must be created on first resume-mode run");

        var snapshotContents = await File.ReadAllTextAsync(snapshotPath);
        Assert.Contains("scratch.log", snapshotContents, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Resume-mode run with existing snapshot reuses it unchanged
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunTaskAsync_ResumeWithExistingSnapshot_ReusesItUnchanged()
    {
        // When a task already has a pre-run-untracked.txt from a prior run
        // instance, a resume must reuse that snapshot as-is — it is the
        // first-instance baseline that spans across the interruption.
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", [], enableFixVerify: false);
        repo.WriteTask("reuse-baseline", "# Reuse baseline task\n");
        var sim = RelayDriverTestHelpers.InitTestRepo(repo);

        // Run 1: flag at stage 9 to create pre-run-untracked.txt in the task dir.
        var flagRunner = new FlagAtStageSubagentRunner(
            flagAtStage: 9,
            inner: new NewTestFileNotInManifestRunner());
        var driver1 = new RelayDriver(
            RelayDriverDependencies.ForTests(
                flagRunner,
                new ScriptedTestRunner(new TestRunResult(1, "red")),
                new InMemoryRelayEventSink(),
                sim),
            new RelayDriverOptions(CreateGitCommit: true));
        await driver1.RunTaskAsync(repo.Root, "reuse-baseline");

        var taskDir = Path.Combine(repo.Root, ".relay", "reuse-baseline");
        var snapshotPath = Path.Combine(taskDir, "pre-run-untracked.txt");
        Assert.True(File.Exists(snapshotPath), "snapshot must exist after first run");

        // Modify the snapshot with a known entry that isn't a real file,
        // so we can detect whether the snapshot was reused or re-captured.
        var forgedContent = "forged-baseline.log" + Environment.NewLine;
        await File.WriteAllTextAsync(snapshotPath, forgedContent);

        // Run 2: resume must reuse the existing snapshot, not capture a fresh one.
        var runner2 = new FileWritingSubagentRunner(
            new ScriptedSubagentRunner(), stage: 6, "src/app.cs", "implemented");
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner2,
                new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(),
                sim),
            new RelayDriverOptions(CreateGitCommit: true, Resume: true));

        var outcome = await driver2.RunTaskAsync(repo.Root, "reuse-baseline");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // The snapshot must still exist and its content must be unchanged.
        Assert.True(File.Exists(snapshotPath));
        var snapshotContents = await File.ReadAllTextAsync(snapshotPath);
        Assert.Equal(forgedContent, snapshotContents);
    }
}
