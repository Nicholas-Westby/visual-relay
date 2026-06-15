using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class WorktreeFilterTests
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

    // ═══════════════════════════════════════════════════════════════
    // Non-test dirty tracked files are reverted to HEAD
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_RevertsNonTestTrackedModifications()
    {
        using var repo = TestRepository.Create();
        var prodFile = await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");
        var testFile = Path.Combine(repo.Root, "tests", "app.tests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        await File.WriteAllTextAsync(testFile, "// test");

        // Modify the production file.
        await File.WriteAllTextAsync(prodFile, "modified by agent");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["tests/app.tests.cs"], tasksDir: null, CancellationToken.None);

        // Production file reverted.
        Assert.Equal("original", await File.ReadAllTextAsync(prodFile));
        // Test file untouched.
        Assert.Equal("// test", await File.ReadAllTextAsync(testFile));
    }

    // ═══════════════════════════════════════════════════════════════
    // Non-test untracked files are deleted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_DeletesNonTestUntrackedFiles()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // Agent created an untracked production file.
        var untrackedProd = Path.Combine(repo.Root, "src", "helper.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(untrackedProd)!);
        await File.WriteAllTextAsync(untrackedProd, "helper");

        // Agent created an untracked test file (should survive).
        var testFile = Path.Combine(repo.Root, "tests", "app.tests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        await File.WriteAllTextAsync(testFile, "// test");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["tests/app.tests.cs"], tasksDir: null, CancellationToken.None);

        // Non-test untracked deleted.
        Assert.False(File.Exists(untrackedProd), "non-test untracked file should be deleted");
        // Test file preserved.
        Assert.True(File.Exists(testFile), "test file should survive");
    }

    // ═══════════════════════════════════════════════════════════════
    // TestFiles paths are left untouched (tracked or untracked)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_LeavesTestFilesUntouched()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // Create a tracked test file that is dirty.
        var trackedTest = Path.Combine(repo.Root, "tests", "app.tests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(trackedTest)!);
        await File.WriteAllTextAsync(trackedTest, "old test");
        TestGit.Run(repo.Root, "add", "tests/app.tests.cs");
        TestGit.Run(repo.Root, "commit", "-m", "add test file");
        // Now modify it (simulating agent authoring a test).
        await File.WriteAllTextAsync(trackedTest, "// updated test");

        // Also create an untracked production stub (should be deleted).
        var stub = Path.Combine(repo.Root, "src", "stub.cs");
        await File.WriteAllTextAsync(stub, "stub");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["tests/app.tests.cs"], tasksDir: null, CancellationToken.None);

        // Tracked test file is NOT reverted.
        Assert.Equal("// updated test", await File.ReadAllTextAsync(trackedTest));
        // Non-test stub is deleted.
        Assert.False(File.Exists(stub), "non-test stub should be deleted");
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal artifacts (.relay/) are preserved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_PreservesInternalArtifacts()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // An untracked file under .relay/ (internal artifact).
        var artifactPath = Path.Combine(repo.Root, ".relay", "task-123", "ledger.md");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        await File.WriteAllTextAsync(artifactPath, "# Ledger");

        // Also a .relay-scratch/ file.
        var scratchPath = Path.Combine(repo.Root, ".relay-scratch", "temp.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(scratchPath)!);
        await File.WriteAllTextAsync(scratchPath, "scratch");

        // And a .swival/ file.
        var swivalPath = Path.Combine(repo.Root, ".swival", "cache");
        Directory.CreateDirectory(Path.GetDirectoryName(swivalPath)!);
        await File.WriteAllTextAsync(swivalPath, "cache");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

        Assert.True(File.Exists(artifactPath), ".relay/ artifact should be preserved");
        Assert.True(File.Exists(scratchPath), ".relay-scratch/ artifact should be preserved");
        Assert.True(File.Exists(swivalPath), ".swival/ artifact should be preserved");
    }

    // ═══════════════════════════════════════════════════════════════
    // Tasks-dir files are preserved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_PreservesTasksDirFiles()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        var tasksDir = "llm-tasks";
        var taskFilePath = Path.Combine(repo.Root, tasksDir, "task-001.md");
        Directory.CreateDirectory(Path.GetDirectoryName(taskFilePath)!);
        await File.WriteAllTextAsync(taskFilePath, "# Task");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: tasksDir, CancellationToken.None);

        Assert.True(File.Exists(taskFilePath), "tasks-dir file should be preserved");
    }

    // ═══════════════════════════════════════════════════════════════
    // Clean tree — idempotent no-op
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_CleanTree_IsNoOp()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");
        // Tree is clean.

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

        Assert.Empty(result.TrackedDiscarded);
        Assert.Empty(result.UntrackedDeleted);
    }

}
