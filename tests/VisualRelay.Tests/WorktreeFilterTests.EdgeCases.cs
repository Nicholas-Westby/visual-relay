using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Edge-case and result-accuracy tests for <see cref="WorktreeFilter"/>.
/// </summary>
public sealed partial class WorktreeFilterTests
{
    // ═══════════════════════════════════════════════════════════════
    // Result lists are accurate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_ResultListsAreAccurate()
    {
        using var repo = TestRepository.Create();
        var prodFile = await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // Modify a tracked production file.
        await File.WriteAllTextAsync(prodFile, "modified");

        // Create an untracked production file.
        var newProd = Path.Combine(repo.Root, "src", "helper.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(newProd)!);
        await File.WriteAllTextAsync(newProd, "helper");

        // Create an untracked test file (should be kept).
        var testFile = Path.Combine(repo.Root, "tests", "app.tests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        await File.WriteAllTextAsync(testFile, "// test");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["tests/app.tests.cs"], tasksDir: null, CancellationToken.None);

        Assert.Contains("src/app.cs", result.TrackedDiscarded, StringComparer.Ordinal);
        Assert.Contains("src/helper.cs", result.UntrackedDeleted, StringComparer.Ordinal);
        Assert.DoesNotContain("tests/app.tests.cs", result.TrackedDiscarded, StringComparer.Ordinal);
        Assert.DoesNotContain("tests/app.tests.cs", result.UntrackedDeleted, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Staged changes reverted (index + working tree reset)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_RevertsStagedChanges()
    {
        using var repo = TestRepository.Create();
        var filePath = await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");
        // Modify and stage.
        await File.WriteAllTextAsync(filePath, "staged");
        TestGit.Run(repo.Root, "add", "src/app.cs");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

        // Working tree reverted.
        Assert.Equal("original", await File.ReadAllTextAsync(filePath));
        // Staging area clean.
        var diffCached = TestGit.Run(repo.Root, "diff", "--cached", "--name-only");
        Assert.Empty(diffCached.Trim());
    }

    // ═══════════════════════════════════════════════════════════════
    // Empty testFiles list discards all dirty files (except artifacts)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_EmptyTestFiles_DiscardsAllDirtyExceptArtifacts()
    {
        using var repo = TestRepository.Create();
        var prodFile = await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");
        await File.WriteAllTextAsync(prodFile, "modified");

        var untracked = Path.Combine(repo.Root, "src", "untracked.cs");
        await File.WriteAllTextAsync(untracked, "untracked");

        // Internal artifact should survive.
        var artifact = Path.Combine(repo.Root, ".relay", "test-task", "note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, "note");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

        Assert.Equal("original", await File.ReadAllTextAsync(prodFile));
        Assert.False(File.Exists(untracked), "untracked file should be deleted");
        Assert.True(File.Exists(artifact), "internal artifact should survive");
    }

    // ═══════════════════════════════════════════════════════════════
    // Multiple testFiles — none discarded
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_MultipleTestFiles_AllPreserved()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        var test1 = Path.Combine(repo.Root, "tests", "unit", "a.tests.cs");
        var test2 = Path.Combine(repo.Root, "tests", "unit", "b.tests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(test1)!);
        await File.WriteAllTextAsync(test1, "// a");
        await File.WriteAllTextAsync(test2, "// b");

        // Modify a production file (should be reverted).
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), "modified");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["tests/unit/a.tests.cs", "tests/unit/b.tests.cs"],
            tasksDir: null, CancellationToken.None);

        Assert.True(File.Exists(test1), "test file a should survive");
        Assert.True(File.Exists(test2), "test file b should survive");
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "app.cs")));
    }

    // ═══════════════════════════════════════════════════════════════
    // Mixed: tracked + untracked non-test files discarded
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_MixedTrackedAndUntracked_Discarded()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");
        await InitRepoWithTrackedFile(repo.Root, "src/util.cs", "util");

        // Both tracked files modified.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), "modified app");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "util.cs"), "modified util");

        // New untracked non-test file.
        var untracked = Path.Combine(repo.Root, "src", "new.cs");
        await File.WriteAllTextAsync(untracked, "new");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

        Assert.Equal(2, result.TrackedDiscarded.Count);
        Assert.Single(result.UntrackedDeleted);
    }
}
