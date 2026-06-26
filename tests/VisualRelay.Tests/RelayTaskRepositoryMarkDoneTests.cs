using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <see cref="RelayTaskRepository.MarkDoneAsync"/> — the public
/// non-committing wrapper around <see cref="TaskCompletionArchive.RetireAsync"/>.
/// </summary>
public sealed class RelayTaskRepositoryMarkDoneTests
{
    [Fact]
    public async Task MarkDoneAsync_ArchiveOnDone_MovesFileUnderCompleted()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], archiveOnDone: true);
        repo.WriteNestedTask("windows-support", "# Windows Support\n\nCross-platform fixes.");

        var repository = new RelayTaskRepository(repo.Root);

        // Before: task is pending.
        var pendingBefore = await repository.ListPendingAsync();
        Assert.Contains(pendingBefore, t => t.Id == "windows-support");

        var task = pendingBefore.Single(t => t.Id == "windows-support");
        Assert.False(task.IsArchived);
        Assert.Equal("Pending", task.StateLabel);

        // Act: retire via MarkDoneAsync (no git commit).
        var destinationPath = await repository.MarkDoneAsync(task);

        Assert.NotNull(destinationPath);
        Assert.StartsWith(
            Path.Combine(repo.Root, "llm-tasks", "completed"),
            destinationPath,
            StringComparison.Ordinal);

        // After: task is gone from pending list.
        var pendingAfter = await repository.ListPendingAsync();
        Assert.DoesNotContain(pendingAfter, t => t.Id == "windows-support");

        // After: task appears in completed list as archived.
        var completed = await repository.ListCompletedAsync();
        var archived = Assert.Single(completed, t => t.Id == "windows-support");
        Assert.True(archived.IsArchived);
        Assert.Equal("Completed", archived.StateLabel);
    }

    [Fact]
    public async Task MarkDoneAsync_UnloadedRepo_ReturnsNull()
    {
        using var repo = TestRepository.Create();
        // No config written — repo is "unloaded" (TryLoadAsync returns Defaulted).
        // Write a nested task so the repo has something to operate on.
        repo.WriteNestedTask("orphan-task", "# Orphan\n");

        var repository = new RelayTaskRepository(repo.Root);
        var pending = await repository.ListPendingAsync();
        var task = Assert.Single(pending, t => t.Id == "orphan-task");

        var result = await repository.MarkDoneAsync(task);

        // Without a loaded config, MarkDoneAsync must return null.
        Assert.Null(result);
    }

    [Fact]
    public async Task MarkDoneAsync_PreservesNestedDirectory_UnderCompleted()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], archiveOnDone: true);
        repo.WriteNestedTask("nested-feature", "# Nested Feature\n",
            ("notes.txt", "design notes"), ("screenshot.png", "fake png"));

        var repository = new RelayTaskRepository(repo.Root);
        var task = (await repository.ListPendingAsync()).Single(t => t.Id == "nested-feature");
        Assert.True(task.IsNested);

        var destinationPath = await repository.MarkDoneAsync(task);
        Assert.NotNull(destinationPath);

        // The nested folder should now live under completed/.
        var completedDir = Path.Combine(repo.Root, "llm-tasks", "completed", "nested-feature");
        Assert.True(Directory.Exists(completedDir),
            $"expected nested task dir at {completedDir}");

        // DONE- prefix on the canonical markdown.
        Assert.True(File.Exists(Path.Combine(completedDir, "DONE-nested-feature.md")));
        Assert.True(File.Exists(Path.Combine(completedDir, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(completedDir, "screenshot.png")));

        // Original directory must be gone.
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "llm-tasks", "nested-feature")));
    }
}
