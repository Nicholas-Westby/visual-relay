using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class WorktreeResetterTests
{
    /// <summary>
    /// Helper: initializes a git repo in <paramref name="root"/> with a single
    /// tracked file committed, then returns the absolute path to that file.
    /// </summary>
    private static async Task<string> InitRepoWithTrackedFile(string root, string relPath, string content)
    {
        var dir = Path.GetDirectoryName(Path.Combine(root, relPath))!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(root, relPath), content);
        TestGit.Run(root, "init");
        TestGit.Run(root, "config", "user.email", "test@example.test");
        TestGit.Run(root, "config", "user.name", "Test");
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-m", "seed");
        return Path.Combine(root, relPath);
    }

    /// <summary>
    /// Writes a pre-run-untracked snapshot file to .relay/{taskId}/pre-run-untracked.txt.
    /// </summary>
    private static async Task WritePreRunSnapshot(string root, string taskId, params string[] entries)
    {
        var dir = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(dir);
        var content = string.Join(Environment.NewLine, entries);
        if (content.Length > 0)
            content += Environment.NewLine;
        await File.WriteAllTextAsync(Path.Combine(dir, "pre-run-untracked.txt"), content);
    }

    // ═══════════════════════════════════════════════════════════════
    // Tracked modifications reverted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetAsync_RevertsTrackedModifications()
    {
        using var repo = TestRepository.Create();
        var filePath = await InitRepoWithTrackedFile(repo.Root, "src/foo.cs", "original");
        // Modify the tracked file.
        await File.WriteAllTextAsync(filePath, "modified");

        await WorktreeResetter.ResetAsync(repo.Root, "test-task", tasksDir: null, CancellationToken.None);

        var actual = await File.ReadAllTextAsync(filePath);
        Assert.Equal("original", actual);
    }

    // ═══════════════════════════════════════════════════════════════
    // Authored untracked files removed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetAsync_RemovesAuthoredUntrackedFile()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/foo.cs", "content");
        // Write an empty pre-run snapshot (nothing was untracked before this task).
        await WritePreRunSnapshot(repo.Root, "test-task");
        // Create a new untracked file.
        var untrackedPath = Path.Combine(repo.Root, "src", "new.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(untrackedPath)!);
        await File.WriteAllTextAsync(untrackedPath, "new file");

        await WorktreeResetter.ResetAsync(repo.Root, "test-task", tasksDir: null, CancellationToken.None);

        Assert.False(File.Exists(untrackedPath), "authored untracked file should be deleted");
    }

    // ═══════════════════════════════════════════════════════════════
    // Pre-existing untracked files preserved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetAsync_PreservesPreExistingUntrackedFiles()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/foo.cs", "content");
        // Create a pre-existing untracked file.
        var preExistingPath = Path.Combine(repo.Root, "pre-existing.log");
        await File.WriteAllTextAsync(preExistingPath, "pre-existing");
        // The snapshot lists it as pre-run.
        await WritePreRunSnapshot(repo.Root, "test-task", "pre-existing.log");

        await WorktreeResetter.ResetAsync(repo.Root, "test-task", tasksDir: null, CancellationToken.None);

        Assert.True(File.Exists(preExistingPath), "pre-existing untracked file should be preserved");
    }

    // ═══════════════════════════════════════════════════════════════
    // Relay artifacts preserved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetAsync_PreservesRelayArtifacts()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/foo.cs", "content");
        // Create a .relay artifact file after run start.
        var artifactDir = Path.Combine(repo.Root, ".relay", "test-task");
        Directory.CreateDirectory(artifactDir);
        var needsReviewPath = Path.Combine(artifactDir, "NEEDS-REVIEW");
        await File.WriteAllTextAsync(needsReviewPath, "needs review reason");

        await WorktreeResetter.ResetAsync(repo.Root, "test-task", tasksDir: null, CancellationToken.None);

        Assert.True(File.Exists(needsReviewPath), ".relay/ artifact should be preserved");
    }

    // ═══════════════════════════════════════════════════════════════
    // Tasks-dir files preserved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetAsync_PreservesTasksDirFiles()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/foo.cs", "content");
        // Create a file under llm-tasks/.
        var tasksDir = "llm-tasks";
        var tasksFilePath = Path.Combine(repo.Root, tasksDir, "task.md");
        Directory.CreateDirectory(Path.GetDirectoryName(tasksFilePath)!);
        await File.WriteAllTextAsync(tasksFilePath, "# task");

        await WorktreeResetter.ResetAsync(repo.Root, "test-task", tasksDir: tasksDir, CancellationToken.None);

        Assert.True(File.Exists(tasksFilePath), "tasks-dir file should be preserved");
    }

    // ═══════════════════════════════════════════════════════════════
    // Missing pre-run snapshot — fallback to empty set (conservative)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetAsync_MissingPreRunSnapshot_FallsBackToEmptySet()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/foo.cs", "content");
        // Do NOT write a pre-run-untracked.txt.
        // Create a new untracked file.
        var untrackedPath = Path.Combine(repo.Root, "src", "untracked.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(untrackedPath)!);
        await File.WriteAllTextAsync(untrackedPath, "new untracked");

        await WorktreeResetter.ResetAsync(repo.Root, "test-task", tasksDir: null, CancellationToken.None);

        // Conservative fallback: deletes every new untracked non-artifact file.
        Assert.False(File.Exists(untrackedPath), "untracked file should be deleted when snapshot is missing");
    }

    // ═══════════════════════════════════════════════════════════════
    // Non-git repo — must not throw
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetAsync_NonGitRepo_DoesNotThrow()
    {
        using var repo = TestRepository.Create();
        // No git init — just a plain temp directory.

        var exception = await Record.ExceptionAsync(
            () => WorktreeResetter.ResetAsync(repo.Root, "test-task", tasksDir: null, CancellationToken.None));

        Assert.Null(exception);
    }

    // ═══════════════════════════════════════════════════════════════
    // Clean tree — idempotent no-op
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetAsync_CleanTree_IsNoOp()
    {
        using var repo = TestRepository.Create();
        var filePath = await InitRepoWithTrackedFile(repo.Root, "src/foo.cs", "original");
        // Tree is already clean (no modifications, no untracked).

        await WorktreeResetter.ResetAsync(repo.Root, "test-task", tasksDir: null, CancellationToken.None);

        // The tracked file is untouched.
        var actual = await File.ReadAllTextAsync(filePath);
        Assert.Equal("original", actual);
        // No exception was thrown (implicit — Record.ExceptionAsync would catch).
    }

    // ═══════════════════════════════════════════════════════════════
    // Staged changes reverted (index reset)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetAsync_RevertsStagedChanges()
    {
        using var repo = TestRepository.Create();
        var filePath = await InitRepoWithTrackedFile(repo.Root, "src/foo.cs", "original");
        // Modify the tracked file and stage the modification.
        await File.WriteAllTextAsync(filePath, "staged-modification");
        TestGit.Run(repo.Root, "add", "src/foo.cs");

        await WorktreeResetter.ResetAsync(repo.Root, "test-task", tasksDir: null, CancellationToken.None);

        // Working tree must be back to HEAD.
        var actual = await File.ReadAllTextAsync(filePath);
        Assert.Equal("original", actual);

        // Staging area must be clean (no staged diff against HEAD).
        var diffCached = TestGit.Run(repo.Root, "diff", "--cached", "--name-only");
        Assert.Empty(diffCached.Trim());
    }

    // ═══════════════════════════════════════════════════════════════
    // Combination: tracked modification + authored untracked
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetAsync_RevertsTrackedAndRemovesUntracked_Simultaneously()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/foo.cs", "original");
        await WritePreRunSnapshot(repo.Root, "test-task"); // empty snapshot

        // Modify a tracked file.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "foo.cs"), "modified");

        // Create an authored untracked file.
        var untrackedPath = Path.Combine(repo.Root, "tests", "hanging.test");
        Directory.CreateDirectory(Path.GetDirectoryName(untrackedPath)!);
        await File.WriteAllTextAsync(untrackedPath, "while(true){}");

        // Also create a pre-existing untracked file (should survive).
        var preExistingPath = Path.Combine(repo.Root, "scratch.log");
        await File.WriteAllTextAsync(preExistingPath, "pre-existing");
        await WritePreRunSnapshot(repo.Root, "test-task", "scratch.log");

        await WorktreeResetter.ResetAsync(repo.Root, "test-task", tasksDir: null, CancellationToken.None);

        // Tracked reverted.
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "foo.cs")));
        // Authored untracked deleted.
        Assert.False(File.Exists(untrackedPath), "authored untracked should be deleted");
        // Pre-existing preserved.
        Assert.True(File.Exists(preExistingPath), "pre-existing untracked should survive");
    }
}
