using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverResumeTests
{
    // ── Re-added task detection tests (Stage 5 — Author-tests) ──────────

    [Fact]
    public async Task RunTaskAsync_Resume_AllDoneWithModifiedTaskMd_RunsFreshAndArchivesOldState()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("re-added", "# Original task — first generation\n");

        // Run 1: complete happy-path (creates all-Done state)
        await RunHappyPath(repo, "re-added");

        var taskDir = Path.Combine(repo.Root, ".relay", "re-added");
        Assert.True(File.Exists(Path.Combine(taskDir, "status.json")));
        var oldStatus = StageStatusRecord.Read(taskDir);
        Assert.All(oldStatus, e => Assert.Equal("Done", e.Status));

        var oldSealsPath = Path.Combine(taskDir, "re-added.seals");
        Assert.True(File.Exists(oldSealsPath));
        var oldSealsContent = await File.ReadAllTextAsync(oldSealsPath);

        // Simulate re-add: overwrite task .md with new content
        repo.WriteTask("re-added", "# Completely new task — re-added by generator\n\nDifferent work.\n");

        // Run 2: Resume:true — should detect re-add, archive, run fresh
        var sink2 = new InMemoryRelayEventSink();
        var runner2 = new ArtifactWritingSubagentRunner();
        runner2.SeedHappyPath("src/new-work.cs", "tests/new-work.tests.cs");
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(runner2, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), sink2),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome2 = await driver2.RunTaskAsync(repo.Root, "re-added");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);

        // Archive must exist
        var relayDir = Path.Combine(repo.Root, ".relay");
        var archiveDirs = Directory.GetDirectories(relayDir, "re-added.run-*");
        Assert.Single(archiveDirs);

        // Archive contains old status.json
        Assert.True(File.Exists(Path.Combine(archiveDirs[0], "status.json")));
        var archivedStatus = StageStatusRecord.Read(archiveDirs[0]);
        Assert.All(archivedStatus, e => Assert.Equal("Done", e.Status));

        // Archive contains old seals
        Assert.True(File.Exists(Path.Combine(archiveDirs[0], "re-added.seals")));

        // New run has fresh seal chain starting from n=1
        var newSealsPath = Path.Combine(taskDir, "re-added.seals");
        Assert.True(File.Exists(newSealsPath));
        var newSeals = await File.ReadAllLinesAsync(newSealsPath);
        Assert.True(newSeals.Length >= 11, $"expected ≥ 11 seal entries in fresh run, got {newSeals.Length}");
        Assert.Contains("\"n\":1", newSeals[0], StringComparison.Ordinal);

        // Seal hashes differ from old
        var newSealsContent = await File.ReadAllTextAsync(newSealsPath);
        Assert.NotEqual(oldSealsContent, newSealsContent);

        // Event audit
        var runStartEvent = sink2.Events.FirstOrDefault(e => e.EventName == "run_start");
        Assert.NotNull(runStartEvent);
        Assert.NotNull(runStartEvent!.Data);
        Assert.True(runStartEvent.Data.ContainsKey("fresh"));
        Assert.Equal("prior state archived (re-added task)", runStartEvent.Data["fresh"]);
    }

    [Fact]
    public async Task RunTaskAsync_Resume_AllDoneWithIdenticalTaskMd_RetiresWithoutArchiving()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        const string taskMd = "# Stable task — not re-added\n\nSame content.\n";
        repo.WriteTask("stable-task", taskMd);

        // Run 1: complete happy-path
        await RunHappyPath(repo, "stable-task");

        var taskDir = Path.Combine(repo.Root, ".relay", "stable-task");
        Assert.True(File.Exists(Path.Combine(taskDir, "status.json")));
        var statusAfterRun1 = StageStatusRecord.Read(taskDir);
        Assert.All(statusAfterRun1, e => Assert.Equal("Done", e.Status));

        var sealsPath = Path.Combine(taskDir, "stable-task.seals");
        var sealCountAfterRun1 = (await File.ReadAllLinesAsync(sealsPath)).Length;

        // Run 2: Resume:true, identical .md — must retire silently
        var sink2 = new InMemoryRelayEventSink();
        var runner2 = new ArtifactWritingSubagentRunner();
        runner2.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(runner2, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), sink2),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome2 = await driver2.RunTaskAsync(repo.Root, "stable-task");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);

        // No archive
        var relayDir = Path.Combine(repo.Root, ".relay");
        Assert.Empty(Directory.GetDirectories(relayDir, "stable-task.run-*"));

        // No fresh: indicator
        var runStartEvent = sink2.Events.FirstOrDefault(e => e.EventName == "run_start");
        Assert.NotNull(runStartEvent);
        Assert.False(runStartEvent!.Data?.ContainsKey("fresh") == true);

        // No stage re-execution
        Assert.False(File.Exists(Path.Combine(taskDir, "stage1-attempt2.report.json")));
        Assert.False(Directory.Exists(Path.Combine(taskDir, "stage1-attempt2")));

        // Seal chain unchanged
        var sealCountAfterRun2 = (await File.ReadAllLinesAsync(sealsPath)).Length;
        Assert.Equal(sealCountAfterRun1, sealCountAfterRun2);
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
}
