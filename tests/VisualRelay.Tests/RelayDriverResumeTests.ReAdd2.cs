using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverResumeTests
{
    [Fact]
    public async Task RunTaskAsync_Resume_PartialStateWithModifiedTaskMd_ResumesWithoutArchiving()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("partial-task", "# v1 — original\n");

        // Run 1: flag at stage 8 (leaves stages 1-7 Done, 8 Flagged)
        var flagAt8 = new FlagAtStageSubagentRunner(flagAtStage: 8);
        var driver1 = new RelayDriver(
            RelayDriverDependencies.ForTests(flagAt8, new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome1 = await driver1.RunTaskAsync(repo.Root, "partial-task");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome1.Status);

        var taskDir = Path.Combine(repo.Root, ".relay", "partial-task");
        var statusAfterRun1 = StageStatusRecord.Read(taskDir);
        Assert.All(statusAfterRun1.Take(7), e => Assert.Equal("Done", e.Status));
        Assert.Equal("Flagged", statusAfterRun1[7].Status);
        Assert.All(statusAfterRun1.Skip(8), e => Assert.Equal("Waiting", e.Status));

        var sealsPath = Path.Combine(taskDir, "partial-task.seals");
        var sealCountAfterRun1 = (await File.ReadAllLinesAsync(sealsPath)).Length;
        Assert.Equal(7, sealCountAfterRun1);

        // Modify task .md
        repo.WriteTask("partial-task", "# v2 — re-added while mid-run\n\nNew content.\n");

        // Run 2: Resume:true — must resume from stage 8, NOT archive
        // Single green result suffices (no stage-5 author gate on resume from stage 8).
        var sink2 = new InMemoryRelayEventSink();
        var happyRunner = new ArtifactWritingSubagentRunner();
        happyRunner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(happyRunner, new ScriptedTestRunner(
                new TestRunResult(0, "green")), sink2),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome2 = await driver2.RunTaskAsync(repo.Root, "partial-task");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);

        // No archive, no fresh: indicator
        var relayDir = Path.Combine(repo.Root, ".relay");
        Assert.Empty(Directory.GetDirectories(relayDir, "partial-task.run-*"));
        var runStartEvent = sink2.Events.FirstOrDefault(e => e.EventName == "run_start");
        Assert.NotNull(runStartEvent);
        Assert.False(runStartEvent!.Data?.ContainsKey("fresh") == true);

        // Stage 8 re-executed on resume
        Assert.True(File.Exists(Path.Combine(taskDir, "stage8-attempt2.report.json")));

        // Seal chain extended
        var sealCountAfterRun2 = (await File.ReadAllLinesAsync(sealsPath)).Length;
        Assert.True(sealCountAfterRun2 > sealCountAfterRun1);
        Assert.True(sealCountAfterRun2 >= 11);

        // All Done after resume
        var statusAfterRun2 = StageStatusRecord.Read(taskDir);
        Assert.All(statusAfterRun2, e => Assert.Equal("Done", e.Status));
    }

    [Fact]
    public async Task RunTaskAsync_Resume_RepeatedReAdds_UsesUniqueArchiveNames()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("multi-add", "# v1 — first incarnation\n");

        // Run 1: complete happy-path
        await RunHappyPath(repo, "multi-add");

        // Re-add 1: v2
        repo.WriteTask("multi-add", "# v2 — second incarnation\n\nNew work v2.\n");
        var sink2 = new InMemoryRelayEventSink();
        var runner2 = new ArtifactWritingSubagentRunner();
        runner2.SeedHappyPath("src/v2.cs", "tests/v2.tests.cs");
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(runner2, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), sink2),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));
        Assert.Equal(RelayTaskOutcomeStatus.Committed,
            (await driver2.RunTaskAsync(repo.Root, "multi-add")).Status);

        // Re-add 2: v3
        repo.WriteTask("multi-add", "# v3 — third incarnation\n\nNew work v3.\n");
        var sink3 = new InMemoryRelayEventSink();
        var runner3 = new ArtifactWritingSubagentRunner();
        runner3.SeedHappyPath("src/v3.cs", "tests/v3.tests.cs");
        var driver3 = new RelayDriver(
            RelayDriverDependencies.ForTests(runner3, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), sink3),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));
        Assert.Equal(RelayTaskOutcomeStatus.Committed,
            (await driver3.RunTaskAsync(repo.Root, "multi-add")).Status);

        // Collision-avoidance: 2 unique archive dirs
        var relayDir = Path.Combine(repo.Root, ".relay");
        var archiveDirs = Directory.GetDirectories(relayDir, "multi-add.run-*");
        Assert.Equal(2, archiveDirs.Length);
        var names = archiveDirs.Select(Path.GetFileName).ToArray();
        Assert.Equal(2, names.Distinct().Count());

        // Each archive contains forensic state
        foreach (var dir in archiveDirs)
        {
            Assert.True(File.Exists(Path.Combine(dir, "status.json")));
            Assert.True(File.Exists(Path.Combine(dir, "multi-add.seals")));
            Assert.True(File.Exists(Path.Combine(dir, "ledger.md")));
        }

        // Both re-adds produced fresh: indicators
        Assert.NotNull(sink2.Events.FirstOrDefault(e => e.EventName == "run_start" && e.Data?.ContainsKey("fresh") == true));
        Assert.NotNull(sink3.Events.FirstOrDefault(e => e.EventName == "run_start" && e.Data?.ContainsKey("fresh") == true));

        // Final task dir has fresh seal chain from n=1
        var finalTaskDir = Path.Combine(repo.Root, ".relay", "multi-add");
        Assert.True(Directory.Exists(finalTaskDir));
        var finalStatus = StageStatusRecord.Read(finalTaskDir);
        Assert.All(finalStatus, e => Assert.Equal("Done", e.Status));
        var finalSeals = await File.ReadAllLinesAsync(Path.Combine(finalTaskDir, "multi-add.seals"));
        Assert.Contains("\"n\":1", finalSeals[0], StringComparison.Ordinal);
    }
}
