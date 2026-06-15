using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

[Collection("GitCommitter")]
public sealed class RelayDriverGitCommitRetirementTests
{
    [Fact]
    public async Task RunTaskAsync_FlatTask_CommitContainsDeleteOfOldAndAddOfDone()
    {
        // archiveOnDone: false — the DONE- file stays flat in llm-tasks/.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: false);
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
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // The commit MUST stage the deletion of the old task file and the
        // addition of the DONE- file in the same commit as the work.
        // --no-renames keeps git from merging the delete+add into a single R100 line.
        var nameStatus = TestGit.Run(repo.Root, "show", "--name-status", "--no-renames", "--pretty=format:", "HEAD");
        Assert.Contains("D\tllm-tasks/ship-status.md", nameStatus, StringComparison.Ordinal);
        Assert.Contains("A\tllm-tasks/DONE-ship-status.md", nameStatus, StringComparison.Ordinal);
        // Filesystem: original gone, DONE- present.
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "DONE-ship-status.md")));
        // DONE- file content matches original.
        var doneContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, "llm-tasks", "DONE-ship-status.md"));
        Assert.Contains("# Ship status", doneContent, StringComparison.Ordinal);
        // git ls-tree confirms the old path is gone from HEAD.
        // Use tab-prefix so "ship-status.md" is not matched inside "DONE-ship-status.md".
        var lsTree = TestGit.Run(repo.Root, "ls-tree", "HEAD", "llm-tasks/");
        Assert.DoesNotContain("\tship-status.md", lsTree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_ArchiveOnDone_CommitsToCompletedBatch()
    {
        // archiveOnDone: true (default) + batch line → archived into completed/batch-N/.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: true);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var nameStatus = TestGit.Run(repo.Root, "show", "--name-status", "--no-renames", "--pretty=format:", "HEAD");
        Assert.Contains("D\tllm-tasks/ship-status.md", nameStatus, StringComparison.Ordinal);
        Assert.Contains("A\tllm-tasks/completed/batch-2/DONE-ship-status.md", nameStatus, StringComparison.Ordinal);
        // Filesystem checks.
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2", "DONE-ship-status.md")));
        // git ls-tree: old path gone, archived path present.
        var lsTree = TestGit.Run(repo.Root, "ls-tree", "-r", "HEAD", "llm-tasks/");
        Assert.DoesNotContain("\tship-status.md", lsTree, StringComparison.Ordinal);
        Assert.Contains("DONE-ship-status.md", lsTree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_NestedTask_RetirementCommitted()
    {
        // Nested task with archiveOnDone: false — only the markdown is renamed.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: false);
        repo.WriteNestedTask("ship-status", "# Ship status\n", ("notes.txt", "some notes"), ("diagram.png", "fake png"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var nameStatus = TestGit.Run(repo.Root, "show", "--name-status", "--no-renames", "--pretty=format:", "HEAD");
        // The nested task's canonical markdown is deleted and a DONE- version added
        // in the same directory. Siblings are untouched.
        Assert.Contains("D\tllm-tasks/ship-status/ship-status.md", nameStatus, StringComparison.Ordinal);
        Assert.Contains("A\tllm-tasks/ship-status/DONE-ship-status.md", nameStatus, StringComparison.Ordinal);
        // Sibling files must NOT appear as additions or deletions (they were untouched).
        Assert.DoesNotContain("notes.txt", nameStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("diagram.png", nameStatus, StringComparison.Ordinal);
        // Filesystem: original gone, DONE- present, siblings intact.
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status", "ship-status.md")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status", "DONE-ship-status.md")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status", "notes.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status", "diagram.png")));
    }

    [Fact]
    public async Task RunTaskAsync_NestedTaskWithArchive_RetirementCommitted()
    {
        // Nested task with archiveOnDone: true — the whole directory moves into
        // completed/batch-N/.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: true);
        repo.WriteNestedTask("ship-status", "batch: 2\n\n# Ship status\n", ("notes.txt", "some notes"), ("diagram.png", "fake png"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var nameStatus = TestGit.Run(repo.Root, "show", "--name-status", "--no-renames", "--pretty=format:", "HEAD");
        // Deletions are for the ORIGINAL tracked filenames (git compares
        // the index against the working tree; it doesn't know about the
        // intermediate MarkDone rename).  Additions are for the post-rename
        // files now living under completed/.
        Assert.Contains("D\tllm-tasks/ship-status/ship-status.md", nameStatus, StringComparison.Ordinal);
        Assert.Contains("D\tllm-tasks/ship-status/notes.txt", nameStatus, StringComparison.Ordinal);
        Assert.Contains("D\tllm-tasks/ship-status/diagram.png", nameStatus, StringComparison.Ordinal);
        Assert.Contains("A\tllm-tasks/completed/batch-2/ship-status/DONE-ship-status.md", nameStatus, StringComparison.Ordinal);
        Assert.Contains("A\tllm-tasks/completed/batch-2/ship-status/notes.txt", nameStatus, StringComparison.Ordinal);
        Assert.Contains("A\tllm-tasks/completed/batch-2/ship-status/diagram.png", nameStatus, StringComparison.Ordinal);
        // Filesystem: old directory gone, archived directory present.
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status")));
        Assert.True(Directory.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2", "ship-status")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2", "ship-status", "DONE-ship-status.md")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2", "ship-status", "notes.txt")));
    }

    [Fact]
    public async Task RunTaskAsync_Idempotent_CompletingAlreadyRetiredTaskSucceeds()
    {
        // First run completes normally. Then we reset git to bring back the
        // original task file while the DONE- file lingers on disk (untracked).
        // The second run must succeed without throwing — idempotent retirement.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: false);
        repo.WriteTask("ship-status", "# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");
        var seedSha = TestGit.Run(repo.Root, "rev-parse", "HEAD").Trim();

        // First run.
        var runner1 = new EditingSubagentRunner();
        var sink1 = new InMemoryRelayEventSink();
        var driver1 = new RelayDriver(
            RelayDriverDependencies.ForTests(runner1, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), sink1),
            RelayDriverOptions.Default);
        var outcome1 = await driver1.RunTaskAsync(repo.Root, "ship-status");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome1.Status);
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "DONE-ship-status.md")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));

        // Simulate a crash-recovery scenario: reset git to the seed commit
        // (soft reset keeps the working tree) then restore the original task
        // file from seed.  This gives us both files on disk — the original
        // (tracked, from seed) and the DONE- (untracked, left over from the
        // first run).
        TestGit.Run(repo.Root, "reset", "--soft", seedSha);
        TestGit.Run(repo.Root, "checkout", seedSha, "--", "llm-tasks/ship-status.md");
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")),
            "original task file should be restored from the seed commit");
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "DONE-ship-status.md")),
            "DONE- file should survive the soft reset (it was never in the seed)");

        // Second run — must succeed without a done_rename_failed event.
        var runner2 = new EditingSubagentRunner();
        var sink2 = new InMemoryRelayEventSink();
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(runner2, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), sink2),
            RelayDriverOptions.Default);
        var outcome2 = await driver2.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);
        Assert.DoesNotContain(sink2.Events, e => e.EventName == "done_rename_failed");
        // HEAD should be unchanged in content (only the second run's commit
        // advances history; the retirement itself is a no-op because DONE- already
        // existed). The DONE- file should still be present.
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "DONE-ship-status.md")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));
    }

    [Fact]
    public async Task RunTaskAsync_GitCheckoutDoesNotResurrectCompletedTask()
    {
        // After a successful run, git checkout of the tasks directory must NOT
        // bring back the original task file — because the retirement rename is
        // part of HEAD.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: false);
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
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "DONE-ship-status.md")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));

        // Simulate a worktree cleanup: checkout the tasks directory from HEAD.
        TestGit.Run(repo.Root, "checkout", "HEAD", "--", "llm-tasks/");

        // The original task file must NOT reappear.
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")),
            "original task file must not be resurrected by git checkout — retirement is part of HEAD");
        // The DONE- file must still exist.
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "DONE-ship-status.md")));
    }

    [Fact]
    public async Task RunTaskAsync_GitCheckoutDoesNotResurrectArchivedTask()
    {
        // Same checkout-resilience test but with archiveOnDone: true.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", [], archiveOnDone: true);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2", "DONE-ship-status.md")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));

        // Checkout the tasks directory from HEAD.
        TestGit.Run(repo.Root, "checkout", "HEAD", "--", "llm-tasks/");

        // The original task file must NOT reappear.
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")),
            "original task file must not be resurrected by git checkout — retirement is part of HEAD");
        // The archived file must still exist.
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2", "DONE-ship-status.md")));
    }
}
