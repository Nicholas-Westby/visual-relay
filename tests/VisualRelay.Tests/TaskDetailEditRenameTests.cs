using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed partial class TaskDetailEditRenameTests
{
    // ── SelectedTaskName ──────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task SelectedTaskName_PopulatedOnSelect()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("feature-x", "# Implement Feature X\n\nThis is the body.\nMore content.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Select the task — SelectedTaskName should be extracted from the # Title line.
        viewModel.SelectedTask = viewModel.Tasks[0];
        // Wait for the asynchronous selection load to finish.
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Implement Feature X", viewModel.SelectedTaskName);
        Assert.NotEqual(string.Empty, viewModel.SelectedTaskName);
    }

    [AvaloniaFact]
    public async Task SelectedTaskName_ClearedOnDeselect()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("some-task", "# Some Title\n\nBody.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Some Title", viewModel.SelectedTaskName);

        // Deselect.
        viewModel.SelectedTask = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Empty, viewModel.SelectedTaskName);
    }

    [AvaloniaFact]
    public async Task SelectedTaskName_FallsBackToId_WhenNoHeading()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Markdown with no # Title line.
        repo.WriteTask("no-heading", "Just some markdown\nwithout a heading.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        // When no # Title exists, fall back to the Id (slug).
        Assert.Equal("no-heading", viewModel.SelectedTaskName);
    }

    // ── Edit splits title and body ────────────────────────────────────────

    [AvaloniaFact]
    public async Task Edit_SplitsTitleAndBody()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("my-task", "# My Task Title\n\nHere is the body.\nMultiple lines.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        // Enter edit mode.
        viewModel.EditSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsEditingMarkdown);
        // Title should be extracted into EditTitleBuffer.
        Assert.Equal("My Task Title", viewModel.EditTitleBuffer);
        // Body should NOT include the # Title line.
        Assert.Equal("Here is the body.\nMultiple lines.", viewModel.EditBuffer);
    }

    [AvaloniaFact]
    public async Task Edit_FillsTitleFromId_WhenNoHeading()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("plain-task", "Just body content.\nNo heading line.");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        viewModel.EditSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsEditingMarkdown);
        // When no # Title, fall back to the slug as the editable title.
        Assert.Equal("plain-task", viewModel.EditTitleBuffer);
        // Entire markdown becomes the body.
        Assert.Equal("Just body content.\nNo heading line.", viewModel.EditBuffer);
    }
}
