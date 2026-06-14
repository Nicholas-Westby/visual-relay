using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayTaskWriterTests
{
    // ── CreateAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_WritesNestedTaskFileAndReturnsPath()
    {
        using var repo = TestRepository.Create();
        var path = await RelayTaskWriter.CreateAsync(repo.Root, "hello-world", "# Hello\n\nWorld.");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.EndsWith($"hello-world{Path.DirectorySeparatorChar}hello-world.md", path, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(repo.Root, "llm-tasks", "hello-world")));
        Assert.Equal("# Hello\n\nWorld.", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CreateAsync_EnsuresTasksDirectoryExists()
    {
        using var repo = TestRepository.Create();
        // No llm-tasks directory exists yet.
        var path = await RelayTaskWriter.CreateAsync(repo.Root, "new-task", "# Body");

        Assert.True(Directory.Exists(Path.Combine(repo.Root, "llm-tasks")));
        Assert.True(Directory.Exists(Path.Combine(repo.Root, "llm-tasks", "new-task")));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CreateAsync_ThrowsWhenSlugIsEmpty()
    {
        using var repo = TestRepository.Create();
        await Assert.ThrowsAsync<ArgumentException>(
            () => RelayTaskWriter.CreateAsync(repo.Root, "", "# Body"));
    }

    [Fact]
    public async Task CreateAsync_ThrowsWhenSlugHasUnsafeCharacters()
    {
        using var repo = TestRepository.Create();
        await Assert.ThrowsAsync<ArgumentException>(
            () => RelayTaskWriter.CreateAsync(repo.Root, "bad/path", "# Body"));
    }

    [Fact]
    public async Task CreateAsync_ThrowsWhenSlugHasReservedPrefix()
    {
        using var repo = TestRepository.Create();
        await Assert.ThrowsAsync<ArgumentException>(
            () => RelayTaskWriter.CreateAsync(repo.Root, "DONE-finished", "# Body"));
    }

    [Fact]
    public async Task CreateAsync_ThrowsWhenSlugCollidesWithExistingNestedTask()
    {
        using var repo = TestRepository.Create();
        // First CreateAsync now writes a nested layout, so the collision
        // is detected via Directory.Exists on the <slug>/ directory.
        await RelayTaskWriter.CreateAsync(repo.Root, "collide", "# First");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RelayTaskWriter.CreateAsync(repo.Root, "collide", "# Second"));
    }

    [Fact]
    public async Task CreateAsync_ThrowsWhenSlugCollidesWithExistingFlatFile()
    {
        using var repo = TestRepository.Create();
        // Create a flat <slug>.md directly (bypass CreateAsync) so we
        // exercise the File.Exists collision path in ValidateSlug.
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        Directory.CreateDirectory(tasksDir);
        await File.WriteAllTextAsync(Path.Combine(tasksDir, "flat-collide.md"), "# Flat");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RelayTaskWriter.CreateAsync(repo.Root, "flat-collide", "# Second"));
    }

    [Fact]
    public async Task CreateAsync_ThrowsWhenSlugCollidesWithExistingFolder()
    {
        using var repo = TestRepository.Create();
        var dir = Path.Combine(repo.Root, "llm-tasks", "collide");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "collide.md"), "# Nested");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RelayTaskWriter.CreateAsync(repo.Root, "collide", "# Flat"));
    }

    // ── SaveAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_WritesMarkdownToTaskMarkdownPath()
    {
        using var repo = TestRepository.Create();
        var path = await RelayTaskWriter.CreateAsync(repo.Root, "mutable", "# Original");
        var task = new RelayTaskItem("mutable", path, Path.GetDirectoryName(path)!, true, []);

        await RelayTaskWriter.SaveAsync(task, "# Updated\n\nNew body.");

        Assert.Equal("# Updated\n\nNew body.", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SaveAsync_WorksForNestedTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("nested", "# Original", ("notes.txt", "hello"));
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var markdownPath = Path.Combine(tasksDir, "nested", "nested.md");
        var task = new RelayTaskItem("nested", markdownPath, Path.GetDirectoryName(markdownPath)!, true,
            [Path.Combine(tasksDir, "nested", "notes.txt")]);

        await RelayTaskWriter.SaveAsync(task, "# Updated\n\nNested edit.");

        Assert.Equal("# Updated\n\nNested edit.", await File.ReadAllTextAsync(markdownPath));
    }

    // ── PromoteToNestedAsync ─────────────────────────────────────────────

    [Fact]
    public async Task PromoteToNestedAsync_MovesFlatMdIntoFolderNamedAfterSlug()
    {
        using var repo = TestRepository.Create();
        // Write a flat file directly (bypass CreateAsync which now writes nested).
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        Directory.CreateDirectory(tasksDir);
        var flatPath = Path.Combine(tasksDir, "promotable.md");
        await File.WriteAllTextAsync(flatPath, "# Promotable");
        var flatTask = new RelayTaskItem("promotable", flatPath, tasksDir, false, []);

        var newMarkdownPath = await RelayTaskWriter.PromoteToNestedAsync(repo.Root, flatTask);

        Assert.NotNull(newMarkdownPath);
        Assert.EndsWith($"promotable{Path.DirectorySeparatorChar}promotable.md", newMarkdownPath, StringComparison.Ordinal);
        Assert.True(File.Exists(newMarkdownPath));
        Assert.False(File.Exists(flatPath)); // old flat file is gone
        Assert.Equal("# Promotable", await File.ReadAllTextAsync(newMarkdownPath));
    }

    [Fact]
    public async Task PromoteToNestedAsync_NoOpsForAlreadyNestedTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("already-nested", "# Body", ("data.txt", "text"));
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var markdownPath = Path.Combine(tasksDir, "already-nested", "already-nested.md");
        var task = new RelayTaskItem("already-nested", markdownPath,
            Path.GetDirectoryName(markdownPath)!, true,
            [Path.Combine(tasksDir, "already-nested", "data.txt")]);

        var result = await RelayTaskWriter.PromoteToNestedAsync(repo.Root, task);

        Assert.Equal(markdownPath, result);
    }

    // ── AddAttachmentAsync ───────────────────────────────────────────────

    [Fact]
    public async Task AddAttachmentAsync_CopiesFileIntoTasksFolder()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("with-attach", "# Task", ("existing.txt", "old"));
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var markdownPath = Path.Combine(tasksDir, "with-attach", "with-attach.md");
        var task = new RelayTaskItem("with-attach", markdownPath,
            Path.GetDirectoryName(markdownPath)!, true,
            [Path.Combine(tasksDir, "with-attach", "existing.txt")]);

        var sourceFile = Path.Combine(repo.Root, "incoming.json");
        await File.WriteAllTextAsync(sourceFile, "{\"ok\":true}");

        var copiedPath = await RelayTaskWriter.AddAttachmentAsync(task, sourceFile);

        Assert.NotNull(copiedPath);
        Assert.EndsWith("incoming.json", copiedPath, StringComparison.Ordinal);
        Assert.True(File.Exists(copiedPath));
        Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(copiedPath));
    }

    [Fact]
    public async Task AddAttachmentAsync_PromotesFlatTaskToNestedBeforeCopying()
    {
        using var repo = TestRepository.Create();
        // Write a flat file directly (bypass CreateAsync which now writes nested).
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        Directory.CreateDirectory(tasksDir);
        var flatPath = Path.Combine(tasksDir, "flat-to-grow.md");
        await File.WriteAllTextAsync(flatPath, "# Flat");
        var flatTask = new RelayTaskItem("flat-to-grow", flatPath, tasksDir, false, []);

        var sourceFile = Path.Combine(repo.Root, "extra.txt");
        await File.WriteAllTextAsync(sourceFile, "attachment body");

        var copiedPath = await RelayTaskWriter.AddAttachmentAsync(flatTask, sourceFile);

        // Flat file should be gone.
        Assert.False(File.Exists(flatPath));
        // Nested layout created.
        var nestedDir = Path.Combine(tasksDir, "flat-to-grow");
        Assert.True(Directory.Exists(nestedDir));
        Assert.True(File.Exists(Path.Combine(nestedDir, "flat-to-grow.md")));
        // Attachment landed.
        Assert.StartsWith(nestedDir, copiedPath, StringComparison.Ordinal);
        Assert.True(File.Exists(copiedPath));
    }

    [Fact]
    public async Task AddAttachmentAsync_PreservesOriginalFileName()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("preserve", "# Task");
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var markdownPath = Path.Combine(tasksDir, "preserve", "preserve.md");
        var task = new RelayTaskItem("preserve", markdownPath,
            Path.GetDirectoryName(markdownPath)!, true, []);

        var sourceFile = Path.Combine(repo.Root, "my.file.txt");
        await File.WriteAllTextAsync(sourceFile, "content");

        var copiedPath = await RelayTaskWriter.AddAttachmentAsync(task, sourceFile);

        Assert.EndsWith("my.file.txt", copiedPath, StringComparison.Ordinal);
    }

    // ── RemoveAttachment ─────────────────────────────────────────────────

    [Fact]
    public void RemoveAttachment_DeletesFileAndReturnsTrue()
    {
        using var repo = TestRepository.Create();
        var filePath = Path.Combine(repo.Root, "to-remove.txt");
        File.WriteAllText(filePath, "garbage");

        var removed = RelayTaskWriter.RemoveAttachment(filePath);

        Assert.True(removed);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void RemoveAttachment_ReturnsFalseWhenFileDoesNotExist()
    {
        using var repo = TestRepository.Create();
        var nonExistent = Path.Combine(repo.Root, "nonexistent.txt");

        var removed = RelayTaskWriter.RemoveAttachment(nonExistent);

        Assert.False(removed);
    }
}
