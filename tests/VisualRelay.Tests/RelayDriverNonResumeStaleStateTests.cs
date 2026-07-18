using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the non-resume stale-state guard: when a fresh (non-resume) run
/// finds an all-Done <c>.relay/&lt;taskId&gt;/status.json</c> left over from
/// a prior completed same-name task, the guard archives the stale state and
/// runs the task fresh from stage 1.
/// </summary>
public sealed class RelayDriverNonResumeStaleStateTests
{
    /// <summary>
    /// When a task completes and the user later creates a new task with the
    /// same slug, a non-resume run must detect the stale all-Done
    /// <c>.relay/&lt;taskId&gt;/</c> state, archive it, and start fresh from
    /// stage 1 — rather than skipping all stages or showing phantom metrics.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_NonResume_StaleAllDoneState_ArchivesAndRunsFresh()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("same-name", "# First incarnation\n\nOriginal content.\n");

        var sim = RelayDriverTestHelpers.InitTestRepo(repo);

        // Run 1: complete happy-path (creates all-Done state under .relay/same-name/).
        // The .relay/<taskId>/ directory survives completion (no cleanup).
        await RelayDriverTestHelpers.RunHappyPath(repo, sim, "same-name");

        var taskDir = Path.Combine(repo.Root, ".relay", "same-name");
        Assert.True(File.Exists(Path.Combine(taskDir, "status.json")));
        var oldStatus = StageStatusRecord.Read(taskDir);
        RelayDriverTestHelpers.AssertHappyPathStatuses(oldStatus);

        // Simulate the user creating a new task with the same slug (overwrites
        // the .md with new content — same as re-creating from a template).
        repo.WriteTask("same-name", "# Second incarnation — re-created by user\n\nDifferent work.\n");

        // Run 2: non-resume — must detect the stale all-Done state and run fresh.
        var sink2 = new InMemoryRelayEventSink();
        var runner2 = new ArtifactWritingSubagentRunner();
        runner2.SeedHappyPath("src/second.cs", "tests/second.tests.cs");
        var driver2 = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner2, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), sink2),
            RelayDriverOptions.NoGitCommit);

        var outcome2 = await driver2.RunTaskAsync(repo.Root, "same-name");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);

        // The stale .relay/same-name/ must have been archived.
        var relayDir = Path.Combine(repo.Root, ".relay");
        var archiveDirs = Directory.GetDirectories(relayDir, "same-name.run-*");
        Assert.Single(archiveDirs);

        // Archive contains the old status.json.
        Assert.True(File.Exists(Path.Combine(archiveDirs[0], "status.json")));
        var archivedStatus = StageStatusRecord.Read(archiveDirs[0]);
        RelayDriverTestHelpers.AssertHappyPathStatuses(archivedStatus);

        // Fresh run produced new status.json in the original task dir.
        Assert.True(File.Exists(Path.Combine(taskDir, "status.json")));
        var newStatus = StageStatusRecord.Read(taskDir);
        Assert.NotEmpty(newStatus);
        // After completing the fresh run, all stages are Done.
        RelayDriverTestHelpers.AssertHappyPathStatuses(newStatus);

        // run_start event must carry the "fresh" indicator.
        var runStartEvent = sink2.Events.FirstOrDefault(e => e.EventName == "run_start");
        Assert.NotNull(runStartEvent);
        Assert.NotNull(runStartEvent!.Data);
        Assert.True(runStartEvent.Data.ContainsKey("fresh"));
        Assert.Equal("prior state archived (re-added task)", runStartEvent.Data["fresh"]);
    }

    /// <summary>
    /// End-to-end: a completed task leaves .relay/ state. A new task with the
    /// same slug (different content) and a non-resume run must see a clean
    /// start — archive of old state, fresh seal chain, and "fresh" event.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_Completion_LeavesRelayDir_SameNameStartsFresh()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("fresh-after-done", "# First run\n\nOriginal.\n");

        var sim = RelayDriverTestHelpers.InitTestRepo(repo);

        // Run 1: complete happy-path. .relay/fresh-after-done/ survives.
        await RelayDriverTestHelpers.RunHappyPath(repo, sim, "fresh-after-done");

        var relayDir = Path.Combine(repo.Root, ".relay");
        var taskDir = Path.Combine(relayDir, "fresh-after-done");
        Assert.True(Directory.Exists(taskDir),
            ".relay/<taskId>/ must survive completion for forensic replay");

        var oldSealsPath = Path.Combine(taskDir, "fresh-after-done.seals");
        Assert.True(File.Exists(oldSealsPath));

        // Re-create a new task with the same slug (different content).
        repo.WriteTask("fresh-after-done", "# Second run — re-created\n\nNew work.\n");

        // Run 2: non-resume — stale-state guard archives prior state, runs fresh.
        var sink2 = new InMemoryRelayEventSink();
        var runner2 = new ArtifactWritingSubagentRunner();
        runner2.SeedHappyPath("src/second.cs", "tests/second.tests.cs");
        var driver2 = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner2, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), sink2),
            RelayDriverOptions.NoGitCommit);

        var outcome2 = await driver2.RunTaskAsync(repo.Root, "fresh-after-done");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);

        // No stale-state interference: archive dir at .relay/ root level.
        var archiveDirs = Directory.GetDirectories(relayDir, "fresh-after-done.run-*");
        Assert.Single(archiveDirs);

        // Fresh run has its own seal chain (n:1 start).
        var newSealsPath = Path.Combine(taskDir, "fresh-after-done.seals");
        Assert.True(File.Exists(newSealsPath));
        var newSeals = await File.ReadAllLinesAsync(newSealsPath);
        Assert.Contains("\"n\":1", newSeals[0], StringComparison.Ordinal);

        // run_start carries fresh indicator.
        var runStartEvent = sink2.Events.FirstOrDefault(e => e.EventName == "run_start");
        Assert.NotNull(runStartEvent);
        Assert.NotNull(runStartEvent!.Data);
        Assert.True(runStartEvent.Data.ContainsKey("fresh"));
    }
}
