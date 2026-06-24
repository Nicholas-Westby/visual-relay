using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed partial class NewTaskAuthoringTests
{
    /// <summary>
    /// Creating a new task with a body must prepend "# {title}\n\n" so
    /// ExtractTitleFromMarkdown finds the heading and the detail panel
    /// shows the entered title, not the slug.
    /// </summary>
    [AvaloniaFact]
    public async Task CreateNewTask_WithBody_PrependsTitleHeading()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Open new-task authoring.
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        viewModel.NewTaskTitle = "My New Task";
        viewModel.NewTaskBody = "This is the task description.";

        Assert.True(viewModel.CreateNewTaskCommand.CanExecute(null));

        await viewModel.CreateNewTaskCommand.ExecuteAsync(null);

        // Assert the written markdown has the heading prepended.
        var expectedDir = Path.Combine(repo.Root, "llm-tasks", "my-new-task");
        var expectedPath = Path.Combine(expectedDir, "my-new-task.md");
        Assert.True(File.Exists(expectedPath));

        var writtenContent = await File.ReadAllTextAsync(expectedPath);
        Assert.Equal("# My New Task\n\nThis is the task description.", writtenContent);
    }
}
