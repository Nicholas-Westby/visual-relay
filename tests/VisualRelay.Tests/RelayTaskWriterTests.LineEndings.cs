using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Task files are tracked: the commit stage force-adds a task file when the task
/// retires. The text arrives with whatever endings its editor used, and on Windows the
/// editor's TextBox inserts CRLF on Enter, so a save of an LF task file that changed one
/// line would otherwise leave every line looking modified. A rewrite keeps the endings
/// the file has, and a new file is LF, as git stores it.
/// </summary>
public sealed partial class RelayTaskWriterTests
{
    [Fact]
    public async Task CreateAsync_TextTypedWithCrlf_IsWrittenLf()
    {
        using var repo = TestRepository.Create();

        var path = await RelayTaskWriter.CreateAsync(repo.Root, "typed-on-windows", "# Typed\r\n\r\nOn Windows.\r\n");

        Assert.Equal("# Typed\n\nOn Windows.\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_OverAnLfFile_WritesLf()
    {
        using var repo = TestRepository.Create();
        var task = await CreateWithBytesAsync(repo.Root, "lf-task", "# Before\n\nBody.\n");

        await RelayTaskWriter.SaveAsync(task, "# After\r\n\r\nBody.\r\n");

        Assert.Equal("# After\n\nBody.\n", await File.ReadAllTextAsync(task.MarkdownPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_OverACrlfFile_KeepsCrlf()
    {
        using var repo = TestRepository.Create();
        var task = await CreateWithBytesAsync(repo.Root, "crlf-task", "# Before\r\n\r\nBody.\r\n");

        await RelayTaskWriter.SaveAsync(task, "# After\n\nBody.\n");

        Assert.Equal("# After\r\n\r\nBody.\r\n", await File.ReadAllTextAsync(task.MarkdownPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenameAsync_KeepsTheEndingsOfTheFileItMoves()
    {
        using var repo = TestRepository.Create();
        var task = await CreateWithBytesAsync(repo.Root, "old-name", "# Old\r\n\r\nBody.\r\n");

        var path = await RelayTaskWriter.RenameAsync(repo.Root, task, "new-name", "# New\n\nBody.\n");

        Assert.Equal("# New\r\n\r\nBody.\r\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenameAsync_ToTheSameSlug_KeepsTheEndingsOfTheFile()
    {
        using var repo = TestRepository.Create();
        var task = await CreateWithBytesAsync(repo.Root, "same-name", "# Old\n\nBody.\n");

        var path = await RelayTaskWriter.RenameAsync(repo.Root, task, "same-name", "# Retitled\r\n\r\nBody.\r\n");

        Assert.Equal("# Retitled\n\nBody.\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    // Seeds the file's bytes directly, so the endings under test are the ones stated
    // here rather than whatever the writer being tested would have chosen.
    private static async Task<RelayTaskItem> CreateWithBytesAsync(string root, string slug, string bytes)
    {
        var dir = Path.Combine(root, "llm-tasks", slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{slug}.md");
        await File.WriteAllTextAsync(path, bytes, TestContext.Current.CancellationToken);
        return new RelayTaskItem(slug, path, dir, true, []);
    }
}
