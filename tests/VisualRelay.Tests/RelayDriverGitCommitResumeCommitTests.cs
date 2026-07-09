using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverGitCommitResumeCommitTests
{
    [Fact]
    public async Task RunTaskAsync_Resume_CommitsFilesAuthoredBeforeInterruption()
    {
        // When a task is interrupted after authoring new files (stages 5–10)
        // and later resumed, the sealed commit MUST include every file authored
        // across ALL run instances — not just the final instance's delta.
        //
        // Bug: RelayDriver re-snapshots preRunUntracked at resume start,
        // classifying the interrupted instance's files as pre-existing and
        // excluding them from auto-include.  HEAD does not build.
        //
        // Fix: persist the first-instance snapshot so files born between
        // first-start and resume-start are auto-include candidates again.
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", []);
        repo.WriteTask("regression-cover", "batch: 3\n\n# Add regression coverage\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed repo");

        // ── Run 1: author new file at stage 5, flag at stage 9 ──────────
        var runner1 = new FlagAtStageSubagentRunner(
            flagAtStage: 9,
            inner: new NewTestFileNotInManifestRunner());
        var driver1 = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner1,
                new ScriptedTestRunner(new TestRunResult(1, "red")), // stage 5 author gate
                new InMemoryRelayEventSink(),
                sim),
            RelayDriverOptions.Default);

        var outcome1 = await driver1.RunTaskAsync(repo.Root, "regression-cover");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome1.Status);

        // The interrupted instance authored the file on disk.
        Assert.True(File.Exists(Path.Combine(repo.Root, "tests", "regression-tests.cs")));

        // ── Run 2: resume with a happy-path runner ──────────────────────
        var runner2 = new NewTestFileNotInManifestRunner();
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner2,
                new ScriptedTestRunner(new TestRunResult(0, "green")), // stage 9 verify
                new InMemoryRelayEventSink(),
                sim),
            new RelayDriverOptions(CreateGitCommit: true, Resume: true));

        var outcome2 = await driver2.RunTaskAsync(repo.Root, "regression-cover");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);

        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains("src/app.cs", committed);
        // KEY: file authored in run 1 must land in the sealed commit.
        Assert.Contains("tests/regression-tests.cs", committed);
    }
}
