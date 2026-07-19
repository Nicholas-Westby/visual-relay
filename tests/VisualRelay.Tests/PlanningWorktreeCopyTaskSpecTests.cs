using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// <see cref="PlanningWorktree.CopyTaskSpecIntoWorktree"/> provisions the pending
/// task's spec from the main repo into a planning worktree. These tests use real
/// temp git repos with the GitSim engine (same pattern as
/// <see cref="PlanningWorktreeConfigCopyTests"/>).
/// </summary>
public sealed class PlanningWorktreeCopyTaskSpecTests
{
    private static void InitRepo(string root) => new GitSimEngine().InitRepo(root);

    private static async Task CommitAll(string root, string message)
    {
        var sim = new GitSimEngine();
        await sim.Git(root, "add", ".");
        sim.Commit(root, message);
    }

    private const string SampleConfig =
        """
        {
          "testCmd": "dotnet test",
          "logSources": [],
          "tasksDir": "llm-tasks"
        }
        """;

    // ───────────────────────────────────────────────────────────────────
    // 1. Folder task with attachment + committed completed dir:
    //    the worktree gets the spec + all siblings, and the worktree-scoped
    //    repository lookup finds the task with the real markdown.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CopyTaskSpec_FolderTaskWithAttachment_ProvisionsIntoWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-tspec-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? worktree = null;
        try
        {
            InitRepo(root);
            // Repo-level .gitignore excludes .relay/ — the normal setup.
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".relay/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");

            // Commit a file under llm-tasks/completed/ so HEAD contains the tasks dir.
            Directory.CreateDirectory(Path.Combine(root, "llm-tasks", "completed", "batch-001"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "llm-tasks", "completed", "batch-001", "DONE-fake.md"),
                "# Fake completed task");
            await CommitAll(root, "seed"); // commits .gitignore + tracked.txt + completed/

            // Write the UNCOMMITTED pending folder task with an attachment.
            var taskDir = Path.Combine(root, "llm-tasks", "spec-task");
            Directory.CreateDirectory(taskDir);
            await File.WriteAllTextAsync(Path.Combine(taskDir, "spec-task.md"), "# Real spec content");
            await File.WriteAllTextAsync(Path.Combine(taskDir, "attachment.png"), "fake-png-data");

            // Write config so RelayTaskRepository can resolve TasksDir.
            Directory.CreateDirectory(Path.Combine(root, ".relay"));
            await File.WriteAllTextAsync(Path.Combine(root, ".relay", "config.json"), SampleConfig);

            // Create the worktree (only committed files appear).
            worktree = await PlanningWorktree.CreateAsync(
                root, "spec-task", "run-tspec", new GitSimEngine(), CancellationToken.None);

            // Sanity: the committed completed dir exists, but the pending task does NOT.
            Assert.True(Directory.Exists(Path.Combine(worktree, "llm-tasks", "completed")),
                "pre-condition: the worktree must have the committed completed dir");
            Assert.False(File.Exists(Path.Combine(worktree, "llm-tasks", "spec-task", "spec-task.md")),
                "pre-condition: the uncommitted pending task must be absent from the worktree");

            // ACT
            await PlanningWorktree.CopyTaskSpecIntoWorktree(
                root, worktree, "spec-task", CancellationToken.None);

            // ASSERT: the spec and attachment are present in the worktree.
            var specPath = Path.Combine(worktree, "llm-tasks", "spec-task", "spec-task.md");
            Assert.True(File.Exists(specPath), "spec markdown must be copied into the worktree");
            Assert.Equal("# Real spec content", await File.ReadAllTextAsync(specPath));

            var attachmentPath = Path.Combine(worktree, "llm-tasks", "spec-task", "attachment.png");
            Assert.True(File.Exists(attachmentPath), "attachment must be copied into the worktree");
            Assert.Equal("fake-png-data", await File.ReadAllTextAsync(attachmentPath));

            // The committed completed dir must still be intact.
            Assert.True(Directory.Exists(Path.Combine(worktree, "llm-tasks", "completed")),
                "committed completed dir must survive the copy");

            // Worktree-scoped repository lookup finds the task with real markdown.
            var worktreeRepo = new RelayTaskRepository(worktree);
            var tasks = await worktreeRepo.ListAsync(includeNeedsReview: true, CancellationToken.None);
            var found = tasks.FirstOrDefault(t => t.Id == "spec-task");
            Assert.NotNull(found);
            Assert.True(found!.IsNested, "folder task must be recognized as nested");
            var input = await worktreeRepo.ReadTaskInputAsync(found, CancellationToken.None);
            Assert.Equal("# Real spec content", input.Markdown);
        }
        finally
        {
            if (worktree is not null)
                await PlanningWorktree.RemoveAsync(root, worktree, new GitSimEngine(), CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. Flat task: a top-level <id>.md lands at the same relative path
    //    in the worktree.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CopyTaskSpec_FlatTask_ProvisionsIntoWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-tspec-flat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".relay/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            await CommitAll(root, "seed");

            // Write the UNCOMMITTED flat task.
            Directory.CreateDirectory(Path.Combine(root, "llm-tasks"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "llm-tasks", "flat-task.md"),
                "# Flat task spec\n\nSome content.");

            // Write config.
            Directory.CreateDirectory(Path.Combine(root, ".relay"));
            await File.WriteAllTextAsync(Path.Combine(root, ".relay", "config.json"), SampleConfig);

            worktree = await PlanningWorktree.CreateAsync(
                root, "flat-task", "run-tspec-flat", new GitSimEngine(), CancellationToken.None);

            // Sanity: the flat task is not in the worktree (uncommitted).
            Assert.False(File.Exists(Path.Combine(worktree, "llm-tasks", "flat-task.md")),
                "pre-condition: the uncommitted flat task must be absent from the worktree");

            // ACT
            await PlanningWorktree.CopyTaskSpecIntoWorktree(
                root, worktree, "flat-task", CancellationToken.None);

            // ASSERT
            var destPath = Path.Combine(worktree, "llm-tasks", "flat-task.md");
            Assert.True(File.Exists(destPath), "flat task markdown must be copied into the worktree");
            Assert.Equal("# Flat task spec\n\nSome content.", await File.ReadAllTextAsync(destPath));

            // Worktree-scoped repository lookup.
            var worktreeRepo = new RelayTaskRepository(worktree);
            var tasks = await worktreeRepo.ListAsync(includeNeedsReview: true, CancellationToken.None);
            var found = tasks.FirstOrDefault(t => t.Id == "flat-task");
            Assert.NotNull(found);
            Assert.False(found!.IsNested, "flat task must not be nested");
            var input = await worktreeRepo.ReadTaskInputAsync(found, CancellationToken.None);
            Assert.Equal("# Flat task spec\n\nSome content.", input.Markdown);
        }
        finally
        {
            if (worktree is not null)
                await PlanningWorktree.RemoveAsync(root, worktree, new GitSimEngine(), CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. Task not found: CopyTaskSpecIntoWorktree is NOT best-effort; when
    //    the task cannot be resolved it throws so PlanOneAsync's per-task
    //    catch converts it to a Failed outcome.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CopyTaskSpec_TaskNotFound_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-tspec-miss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".relay/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            await CommitAll(root, "seed");

            // Write config so the repository has a valid TasksDir.
            Directory.CreateDirectory(Path.Combine(root, ".relay"));
            await File.WriteAllTextAsync(Path.Combine(root, ".relay", "config.json"), SampleConfig);

            worktree = await PlanningWorktree.CreateAsync(
                root, "no-such-task", "run-tspec-miss", new GitSimEngine(), CancellationToken.None);

            // ACT & ASSERT: copying a nonexistent task must throw.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PlanningWorktree.CopyTaskSpecIntoWorktree(
                    root, worktree, "no-such-task", CancellationToken.None));

            Assert.Contains("no-such-task", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (worktree is not null)
                await PlanningWorktree.RemoveAsync(root, worktree, new GitSimEngine(), CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
