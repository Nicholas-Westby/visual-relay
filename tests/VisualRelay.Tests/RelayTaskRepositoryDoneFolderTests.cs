using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

public sealed class RelayTaskRepositoryDoneFolderTests
{
    [Fact]
    public async Task ListPendingAsync_FolderWithOnlyDoneMd_IsAbsentFromPending()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var dir = Path.Combine(repo.Root, "llm-tasks", "add-app-icon");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "DONE-add-app-icon.md"), "# Done task\n");

        var tasks = await new RelayTaskRepository(repo.Root).ListPendingAsync();

        // Neither the stripped nor the raw DONE-prefixed id may appear as pending.
        Assert.DoesNotContain(tasks, t => t.Id == "add-app-icon");
        Assert.DoesNotContain(tasks, t => t.Id == "DONE-add-app-icon");
    }

    [Fact]
    public async Task ListCompletedAsync_FolderWithOnlyDoneMd_AppearsInCompleted()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var dir = Path.Combine(repo.Root, "llm-tasks", "add-app-icon");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "DONE-add-app-icon.md"), "# Done task\n");

        var tasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();

        var task = Assert.Single(tasks, t => t.Id == "add-app-icon");
        Assert.True(task.IsArchived);
        Assert.True(task.IsNested);
        Assert.Null(task.ArchiveBatch);
        Assert.Equal("Completed", task.StateLabel);
    }

    [Fact]
    public async Task ListPendingAsync_FolderWithDoneMdAndRegularMd_PendingFromRegularMd()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var dir = Path.Combine(repo.Root, "llm-tasks", "mixed-folder");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "DONE-old-task.md"), "# Old done task\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "current.md"), "# Current task\n");

        var tasks = await new RelayTaskRepository(repo.Root).ListPendingAsync();

        // The folder emits one pending task from the non-prefixed .md file.
        var task = Assert.Single(tasks);
        Assert.Equal("current", task.Id);
        Assert.True(task.IsNested);
        // DONE-prefixed .md becomes a sibling attachment, not the canonical id.
        Assert.Contains(task.SiblingPaths, s => s.EndsWith("DONE-old-task.md", StringComparison.Ordinal));

        // The folder is still active (has current.md), so the DONE residue must
        // NOT appear in the completed list — only all-DONE folders go there.
        var completed = await new RelayTaskRepository(repo.Root).ListCompletedAsync();
        Assert.DoesNotContain(completed, t => t.Id == "old-task");
    }

    [Fact]
    public async Task ListPendingAsync_NoResultHasSkippedPrefix()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Flat task
        repo.WriteTask("alpha", "# Alpha\n");
        // Nested task with canonical folder-named .md
        repo.WriteNestedTask("beta", "# Beta\n", ("notes.md", "# Notes"));
        // Folder with only DONE-*.md — should be absent from pending
        var doneOnlyDir = Path.Combine(repo.Root, "llm-tasks", "gamma");
        Directory.CreateDirectory(doneOnlyDir);
        await File.WriteAllTextAsync(Path.Combine(doneOnlyDir, "DONE-gamma.md"), "# Done gamma\n");
        // Folder with DONE-*.md + regular .md — pending from the regular file
        var mixedDir = Path.Combine(repo.Root, "llm-tasks", "delta");
        Directory.CreateDirectory(mixedDir);
        await File.WriteAllTextAsync(Path.Combine(mixedDir, "DONE-delta.md"), "# Done delta\n");
        await File.WriteAllTextAsync(Path.Combine(mixedDir, "epsilon.md"), "# Epsilon\n");
        // Top-level DONE-*.md and IGNORE-*.md — skipped by Walk
        repo.WriteTask("DONE-zeta", "# Done zeta\n");
        repo.WriteTask("IGNORE-eta", "# Ignore eta\n");

        var tasks = await new RelayTaskRepository(repo.Root).ListPendingAsync();

        // Invariant: no pending task id starts with DONE- or IGNORE-.
        Assert.All(tasks, t =>
            Assert.False(t.Id.StartsWith("DONE-", StringComparison.OrdinalIgnoreCase)));
        Assert.All(tasks, t =>
            Assert.False(t.Id.StartsWith("IGNORE-", StringComparison.OrdinalIgnoreCase)));
        // Expected pending: alpha (flat), beta (nested), epsilon (from delta folder).
        Assert.Equal(3, tasks.Count);
        Assert.Contains(tasks, t => t.Id == "alpha");
        Assert.Contains(tasks, t => t.Id == "beta");
        Assert.Contains(tasks, t => t.Id == "epsilon");
    }
}
