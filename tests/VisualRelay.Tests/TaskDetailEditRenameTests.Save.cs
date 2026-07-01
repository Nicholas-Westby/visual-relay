using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed partial class TaskDetailEditRenameTests
{
    // ── SaveEdit renames when title changes ───────────────────────────────

    [AvaloniaFact]
    public async Task SaveEdit_RenamesTask_WhenTitleChanges()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("old-name", "# Old Name\n\nOriginal body text.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        // Enter edit mode.
        viewModel.EditSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsEditingMarkdown);
        Assert.Equal("Old Name", viewModel.EditTitleBuffer);

        // Change the title.
        viewModel.EditTitleBuffer = "New Name";
        viewModel.EditBuffer = "Updated body text.";

        // Save.
        await viewModel.SaveEditCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // Edit mode ended.
        Assert.False(viewModel.IsEditingMarkdown);

        // Old paths should NOT exist.
        var oldDir = Path.Combine(repo.Root, "llm-tasks", "old-name");
        Assert.False(Directory.Exists(oldDir),
            "Old task directory should be gone after rename.");
        Assert.False(File.Exists(Path.Combine(oldDir, "old-name.md")),
            "Old markdown file should be gone after rename.");

        // New paths SHOULD exist.
        var newDir = Path.Combine(repo.Root, "llm-tasks", "new-name");
        var newPath = Path.Combine(newDir, "new-name.md");
        Assert.True(Directory.Exists(newDir),
            "New task directory should exist after rename.");
        Assert.True(File.Exists(newPath),
            "New markdown file should exist after rename.");

        // Content should include the new title.
        var content = await File.ReadAllTextAsync(newPath);
        Assert.Equal("# New Name\n\nUpdated body text.", content);

        // Task list should show the new slug.
        Assert.NotNull(viewModel.SelectedTask);
        Assert.Equal("new-name", viewModel.SelectedTask.Id);

        // SelectedTaskName should reflect the new title.
        Assert.Equal("New Name", viewModel.SelectedTaskName);
    }

    [AvaloniaFact]
    public async Task SaveEdit_DoesNotRename_WhenTitleUnchanged()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("stable-slug", "# Stable Title\n\nOriginal body.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        viewModel.EditSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Stable Title", viewModel.EditTitleBuffer);

        // Change only the body, keep the title the same.
        viewModel.EditBuffer = "Updated body only.";

        await viewModel.SaveEditCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.IsEditingMarkdown);

        // Task stays at the same path.
        var tasksDir = Path.Combine(repo.Root, "llm-tasks", "stable-slug");
        var markdownPath = Path.Combine(tasksDir, "stable-slug.md");
        Assert.True(Directory.Exists(tasksDir));
        Assert.True(File.Exists(markdownPath));

        // Content updated in place.
        var content = await File.ReadAllTextAsync(markdownPath);
        Assert.Equal("# Stable Title\n\nUpdated body only.", content);

        // Slug unchanged.
        Assert.NotNull(viewModel.SelectedTask);
        Assert.Equal("stable-slug", viewModel.SelectedTask.Id);
    }

    [AvaloniaFact]
    public async Task SaveEdit_DoesNotRename_WhenSlugMatchesDespiteTitleChange()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Title with extra whitespace/punctuation that slugifies to the same slug.
        repo.WriteTask("my-task", "# My Task!\n\nBody here.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        viewModel.EditSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Change the title to something that slugifies to the SAME slug.
        viewModel.EditTitleBuffer = "My Task?";
        viewModel.EditBuffer = "Body updated.";

        await viewModel.SaveEditCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // Task stays at the same path (slug unchanged).
        var tasksDir = Path.Combine(repo.Root, "llm-tasks", "my-task");
        var markdownPath = Path.Combine(tasksDir, "my-task.md");
        Assert.True(File.Exists(markdownPath));
        Assert.Equal("# My Task?\n\nBody updated.", await File.ReadAllTextAsync(markdownPath));
        Assert.Equal("my-task", viewModel.SelectedTask!.Id);
    }

    // ── CanSaveEdit blocks empty title ────────────────────────────────────

    [AvaloniaFact]
    public async Task SaveEdit_BlocksEmptyTitle()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("some-task", "# Some Task\n\nBody.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        viewModel.EditSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsEditingMarkdown);

        // Empty the title.
        viewModel.EditTitleBuffer = string.Empty;

        Assert.False(viewModel.SaveEditCommand.CanExecute(null),
            "Save should be disabled when EditTitleBuffer is empty.");

        // Whitespace-only title.
        viewModel.EditTitleBuffer = "   ";

        Assert.False(viewModel.SaveEditCommand.CanExecute(null),
            "Save should be disabled when EditTitleBuffer is whitespace.");
    }

    [AvaloniaFact]
    public async Task SaveEdit_Enabled_WhenTitleHasContent()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("some-task", "# Some Task\n\nBody.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        viewModel.EditSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsEditingMarkdown);

        // Title has content — save should be enabled.
        Assert.True(viewModel.SaveEditCommand.CanExecute(null));

        // Even if the body is empty, save should be enabled as long as title is set.
        viewModel.EditBuffer = string.Empty;
        Assert.True(viewModel.SaveEditCommand.CanExecute(null));
    }

    // ── CancelEdit clears title buffer ────────────────────────────────────

    [AvaloniaFact]
    public async Task CancelEdit_ClearsTitleBuffer()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("my-task", "# My Title\n\nBody text.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        viewModel.EditSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("My Title", viewModel.EditTitleBuffer);
        Assert.Equal("Body text.", viewModel.EditBuffer);

        // Cancel edit.
        viewModel.CancelEditCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.IsEditingMarkdown);
        Assert.Equal(string.Empty, viewModel.EditBuffer);
        Assert.Equal(string.Empty, viewModel.EditTitleBuffer);
    }

    // ── Attachments preserved on rename ───────────────────────────────────

    [AvaloniaFact]
    public async Task SaveEdit_PreservesAttachmentsOnRename()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("project-x", "# Project X\n\nTodo.",
            ("design.md", "Architecture notes"), ("mockup.png", "binary"));

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        viewModel.EditSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        viewModel.EditTitleBuffer = "Project Y";
        viewModel.EditBuffer = "Updated todo.";

        await viewModel.SaveEditCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // Old directory gone.
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "llm-tasks", "project-x")));

        // New directory exists with all siblings.
        var newDir = Path.Combine(repo.Root, "llm-tasks", "project-y");
        Assert.True(Directory.Exists(newDir));
        Assert.True(File.Exists(Path.Combine(newDir, "project-y.md")));
        Assert.True(File.Exists(Path.Combine(newDir, "design.md")));
        Assert.True(File.Exists(Path.Combine(newDir, "mockup.png")));
        Assert.Equal("Architecture notes", await File.ReadAllTextAsync(Path.Combine(newDir, "design.md")));
    }
}
