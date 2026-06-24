using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayTaskWriterTests
{
    // ── RenameAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task RenameAsync_RenamesDirectoryAndFileAndUpdatesContent()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("old-slug", "# Old Title\n\nBody here.");
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var oldDir = Path.Combine(tasksDir, "old-slug");
        var oldPath = Path.Combine(oldDir, "old-slug.md");
        var task = new RelayTaskItem("old-slug", oldPath, oldDir, true, []);

        var newPath = await RelayTaskWriter.RenameAsync(repo.Root, task, "new-slug", "# New Title\n\nUpdated body.");

        // New paths exist.
        var expectedDir = Path.Combine(tasksDir, "new-slug");
        var expectedPath = Path.Combine(expectedDir, "new-slug.md");
        Assert.Equal(expectedPath, newPath);
        Assert.True(Directory.Exists(expectedDir));
        Assert.True(File.Exists(expectedPath));

        // Old paths are gone.
        Assert.False(Directory.Exists(oldDir));
        Assert.False(File.Exists(oldPath));

        // Content updated.
        Assert.Equal("# New Title\n\nUpdated body.", await File.ReadAllTextAsync(expectedPath));
    }

    [Fact]
    public async Task RenameAsync_PreservesSiblingFiles()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("project-alpha", "# Project Alpha\n\nTodo.",
            ("notes.txt", "design notes"), ("image.png", "placeholder"));
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var oldDir = Path.Combine(tasksDir, "project-alpha");
        var task = new RelayTaskItem("project-alpha",
            Path.Combine(oldDir, "project-alpha.md"), oldDir, true,
            [Path.Combine(oldDir, "notes.txt"), Path.Combine(oldDir, "image.png")]);

        var newPath = await RelayTaskWriter.RenameAsync(repo.Root, task, "project-beta", "# Project Beta\n\nTodo.");

        var newDir = Path.Combine(tasksDir, "project-beta");
        Assert.True(Directory.Exists(newDir));
        Assert.True(File.Exists(Path.Combine(newDir, "project-beta.md")));
        Assert.True(File.Exists(Path.Combine(newDir, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(newDir, "image.png")));
        Assert.Equal("design notes", await File.ReadAllTextAsync(Path.Combine(newDir, "notes.txt")));
        Assert.Equal("placeholder", await File.ReadAllTextAsync(Path.Combine(newDir, "image.png")));

        // Old directory is gone.
        Assert.False(Directory.Exists(oldDir));
    }

    [Fact]
    public async Task RenameAsync_ThrowsWhenNewSlugIsEmpty()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("valid", "# Valid\n\nBody.");
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var task = new RelayTaskItem("valid",
            Path.Combine(tasksDir, "valid", "valid.md"), Path.Combine(tasksDir, "valid"), true, []);

        await Assert.ThrowsAsync<ArgumentException>(
            () => RelayTaskWriter.RenameAsync(repo.Root, task, "", "# New Title\n\nBody."));
    }

    [Fact]
    public async Task RenameAsync_ThrowsWhenNewSlugHasUnsafeCharacters()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("safe", "# Safe\n\nBody.");
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var task = new RelayTaskItem("safe",
            Path.Combine(tasksDir, "safe", "safe.md"), Path.Combine(tasksDir, "safe"), true, []);

        await Assert.ThrowsAsync<ArgumentException>(
            () => RelayTaskWriter.RenameAsync(repo.Root, task, "bad/slug", "# New\n\nBody."));
    }

    [Fact]
    public async Task RenameAsync_ThrowsWhenNewSlugHasReservedPrefix()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("active", "# Active\n\nBody.");
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var task = new RelayTaskItem("active",
            Path.Combine(tasksDir, "active", "active.md"), Path.Combine(tasksDir, "active"), true, []);

        await Assert.ThrowsAsync<ArgumentException>(
            () => RelayTaskWriter.RenameAsync(repo.Root, task, "DONE-finished", "# Finished\n\nBody."));
    }

    [Fact]
    public async Task RenameAsync_ThrowsWhenNewSlugCollidesWithAnotherTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("existing", "# Existing\n\nBody.");
        repo.WriteNestedTask("rename-me", "# Rename Me\n\nBody.");
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var task = new RelayTaskItem("rename-me",
            Path.Combine(tasksDir, "rename-me", "rename-me.md"),
            Path.Combine(tasksDir, "rename-me"), true, []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RelayTaskWriter.RenameAsync(repo.Root, task, "existing", "# New\n\nBody."));
    }

    [Fact]
    public async Task RenameAsync_DoesNotCollideWithSelf()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("self-rename", "# Self Rename\n\nBody.");
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var task = new RelayTaskItem("self-rename",
            Path.Combine(tasksDir, "self-rename", "self-rename.md"),
            Path.Combine(tasksDir, "self-rename"), true, []);

        // Renaming to the same slug should succeed (no-op rename).
        var newPath = await RelayTaskWriter.RenameAsync(repo.Root, task, "self-rename", "# Self Rename\n\nUpdated body.");

        Assert.EndsWith($"self-rename{Path.DirectorySeparatorChar}self-rename.md", newPath, StringComparison.Ordinal);
        Assert.True(File.Exists(newPath));
        Assert.Equal("# Self Rename\n\nUpdated body.", await File.ReadAllTextAsync(newPath));
    }

    [Fact]
    public async Task RenameAsync_ThrowsWhenNewSlugCollidesWithFlatFile()
    {
        using var repo = TestRepository.Create();
        // Create a flat file at the target slug.
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        Directory.CreateDirectory(tasksDir);
        await File.WriteAllTextAsync(Path.Combine(tasksDir, "flat-target.md"), "# Flat target");

        repo.WriteNestedTask("source", "# Source\n\nBody.");
        var task = new RelayTaskItem("source",
            Path.Combine(tasksDir, "source", "source.md"),
            Path.Combine(tasksDir, "source"), true, []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RelayTaskWriter.RenameAsync(repo.Root, task, "flat-target", "# New\n\nBody."));
    }
}
