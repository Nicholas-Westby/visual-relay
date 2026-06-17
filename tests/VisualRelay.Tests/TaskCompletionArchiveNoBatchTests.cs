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
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Archived at completed/DONE-ship-status.md (no batch dir).
        var archivedPath = Path.Combine(repo.Root, "llm-tasks", "completed", "DONE-ship-status.md");
        Assert.True(File.Exists(archivedPath), $"expected archived file at {archivedPath}");

        // Original must be gone.
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));

        // Commit must stage the archived path.
        var nameStatus = TestGit.Run(repo.Root, "show", "--name-status", "--no-renames", "--pretty=format:", "HEAD");
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
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
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

    // ── Idempotency test ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunTaskAsync_NoBatch_Idempotent_SecondRunDoesNotThrow()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: true);
        repo.WriteTask("ship-status", "# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");
        var seedSha = TestGit.Run(repo.Root, "rev-parse", "HEAD").Trim();

        // First run — archives to completed/DONE-ship-status.md.
        var runner1 = new EditingSubagentRunner();
        var driver1 = new RelayDriver(
            RelayDriverDependencies.ForTests(runner1,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);
        var outcome1 = await driver1.RunTaskAsync(repo.Root, "ship-status");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome1.Status);
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "DONE-ship-status.md")));

        // Simulate crash-recovery: soft-reset to seed to bring back original.
        TestGit.Run(repo.Root, "reset", "--soft", seedSha);
        TestGit.Run(repo.Root, "checkout", seedSha, "--", "llm-tasks/ship-status.md");

        // Second run — must complete without throwing.
        var sink2 = new InMemoryRelayEventSink();
        var runner2 = new EditingSubagentRunner();
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(runner2,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                sink2),
            RelayDriverOptions.Default);
        var outcome2 = await driver2.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);
        Assert.DoesNotContain(sink2.Events, e => e.EventName == "done_rename_failed");
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "DONE-ship-status.md")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));
    }
}
