using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the no-batch archive path: archiveOnDone:true with no batch: line
/// and no existing completed/batch-N dirs should move tasks directly under
/// llm-tasks/completed/ (flat → completed/DONE-id.md, nested → completed/id/).
/// </summary>
public sealed class TaskCompletionArchiveNoBatchTests
{
    // ── Driver tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunTaskAsync_NoBatchLine_ArchiveOnDone_FlatTaskLandsUnderCompleted()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: true);
        // Deliberately NO "batch: N" line.
        repo.WriteTask("ship-status", "# Ship status\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        // The original repo.WriteConfig/WriteTask output must also be tracked at
        // seed (as real `git add .` would) so the task file's later archive-rename
        // shows as a tracked delete, not a file that was never in history.
        await sim.Git(repo.Root, "add", "-A");
        sim.Commit(repo.Root, "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(),
                sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Archived at completed/DONE-ship-status.md (no batch dir).
        var archivedPath = Path.Combine(repo.Root, "llm-tasks", "completed", "DONE-ship-status.md");
        Assert.True(File.Exists(archivedPath), $"expected archived file at {archivedPath}");

        // Original must be gone.
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));

        // Commit must stage the archived path.
        var (_, nameStatus) = await sim.Git(repo.Root, "diff-tree", "--no-commit-id", "--name-status", "-r", sim.Head(repo.Root)!);
        Assert.Contains("A\tllm-tasks/completed/DONE-ship-status.md", nameStatus, StringComparison.Ordinal);
        Assert.Contains("D\tllm-tasks/ship-status.md", nameStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_NoBatchLine_ArchiveOnDone_NestedTaskLandsUnderCompleted()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: true);
        // No "batch: N" line; nested task with sibling files.
        repo.WriteNestedTask("ship-status", "# Ship status\n",
            ("notes.txt", "some notes"), ("diagram.png", "fake png"));
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        // The original repo.WriteConfig/WriteTask output must also be tracked at
        // seed (as real `git add .` would) so the task file's later archive-rename
        // shows as a tracked delete, not a file that was never in history.
        await sim.Git(repo.Root, "add", "-A");
        sim.Commit(repo.Root, "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(),
                sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Whole folder moved to completed/ship-status/.
        var archivedDir = Path.Combine(repo.Root, "llm-tasks", "completed", "ship-status");
        Assert.True(Directory.Exists(archivedDir), $"expected archived dir at {archivedDir}");
        Assert.True(File.Exists(Path.Combine(archivedDir, "DONE-ship-status.md")));
        Assert.True(File.Exists(Path.Combine(archivedDir, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(archivedDir, "diagram.png")));

        // Original folder must be gone.
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status")));
    }

    // ── Repository test ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListCompletedAsync_TaskDirectlyUnderCompleted_AppearsWithNullBatch()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // Place a task directly under completed/ (no batch dir) — the new destination.
        var completedDir = Path.Combine(repo.Root, "llm-tasks", "completed", "ship-status");
        Directory.CreateDirectory(completedDir);
        await File.WriteAllTextAsync(Path.Combine(completedDir, "DONE-ship-status.md"), "# Ship status\n");

        var completedTasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();
        var pendingTasks = await new RelayTaskRepository(repo.Root).ListPendingAsync();

        var task = Assert.Single(completedTasks, t => t.Id == "ship-status");
        Assert.True(task.IsArchived);
        Assert.Equal("Completed", task.StateLabel);
        Assert.Null(task.ArchiveBatch);

        // Must NOT appear in the pending list.
        Assert.DoesNotContain(pendingTasks, t => t.Id == "ship-status");
    }

    [Fact]
    public async Task ListCompletedAsync_FlatFileDirectlyUnderCompleted_AppearsWithNullBatchAndIsNotNested()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // Place a DONE- file flat in completed/ (no subdir) — the destination
        // produced when archiveOnDone:true and no batch number resolves.
        var completedDir = Path.Combine(repo.Root, "llm-tasks", "completed");
        Directory.CreateDirectory(completedDir);
        await File.WriteAllTextAsync(Path.Combine(completedDir, "DONE-ship-status.md"), "# Ship status\n");

        var completedTasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();
        var pendingTasks = await new RelayTaskRepository(repo.Root).ListPendingAsync();

        var task = Assert.Single(completedTasks, t => t.Id == "ship-status");
        Assert.True(task.IsArchived);
        Assert.False(task.IsNested, "flat file directly in completed/ must not be nested");
        Assert.Equal("Completed", task.StateLabel);
        Assert.Null(task.ArchiveBatch);

        // Must NOT appear in the pending list.
        Assert.DoesNotContain(pendingTasks, t => t.Id == "ship-status");
    }

    // ── Idempotency test ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunTaskAsync_NoBatch_Idempotent_SecondRunDoesNotThrow()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: true);
        repo.WriteTask("ship-status", "# Ship status\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        // llm-tasks/ship-status.md must be tracked at seed (as real `git add .`
        // would) so the mid-test checkout below can restore it from this commit.
        await sim.Git(repo.Root, "add", "-A");
        var seedSha = sim.Commit(repo.Root, "chore: seed repo");

        // First run — archives to completed/DONE-ship-status.md.
        var runner1 = new EditingSubagentRunner();
        var driver1 = new RelayDriver(
            RelayDriverDependencies.ForTests(runner1,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(),
                sim),
            RelayDriverOptions.Default);
        var outcome1 = await driver1.RunTaskAsync(repo.Root, "ship-status");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome1.Status);
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "DONE-ship-status.md")));

        // Simulate crash-recovery: soft-reset to seed to bring back original.
        await sim.Git(repo.Root, "reset", "--soft", seedSha);
        await sim.Git(repo.Root, "checkout", seedSha, "--", "llm-tasks/ship-status.md");

        // Second run — must complete without throwing.
        var sink2 = new InMemoryRelayEventSink();
        var runner2 = new EditingSubagentRunner();
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(runner2,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                sink2,
                sim),
            RelayDriverOptions.Default);
        var outcome2 = await driver2.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);
        Assert.DoesNotContain(sink2.Events, e => e.EventName == "done_rename_failed");
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "DONE-ship-status.md")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));
    }
}
