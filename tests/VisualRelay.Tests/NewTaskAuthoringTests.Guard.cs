using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed partial class NewTaskAuthoringTests
{
    /// <summary>
    /// The "New" button (bound to OpenNewTaskDialogCommand) must be disabled
    /// when no project folder has been selected — RootPath is empty or the
    /// directory does not exist. Without this guard, clicking "New" opens the
    /// authoring form and "Create task" can be invoked, which will write files
    /// to a relative path instead of a project root.
    /// </summary>
    [AvaloniaFact]
    public void OpenNewTaskDialogCommand_Disabled_WhenRootPathIsEmpty()
    {
        // Fresh ViewModel with the default (empty) RootPath; no repo on disk.
        var viewModel = new MainWindowViewModel();

        Assert.False(viewModel.OpenNewTaskDialogCommand.CanExecute(null),
            "OpenNewTaskDialogCommand must be disabled when RootPath is empty " +
            "(no project folder selected). Missing CanExecute guard on OpenNewTaskDialog.");
    }

    /// <summary>
    /// The "New" button must also be disabled when RootPath points to a
    /// directory that does not exist on disk (e.g. a stale/missing mount).
    /// The established codebase pattern is Directory.Exists(RootPath) —
    /// used by CanRefresh, CanToggleArchive, and CanBootstrapProject.
    /// </summary>
    [AvaloniaFact]
    public void OpenNewTaskDialogCommand_Disabled_WhenRootPathDoesNotExist()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var viewModel = new MainWindowViewModel { RootPath = nonExistent };

        Assert.False(Directory.Exists(viewModel.RootPath),
            "Sanity: the test path must not exist on disk.");

        Assert.False(viewModel.OpenNewTaskDialogCommand.CanExecute(null),
            "OpenNewTaskDialogCommand must be disabled when RootPath does not " +
            "exist on disk. The guard should use Directory.Exists(RootPath).");
    }

    /// <summary>
    /// Regression: the "New" button must be enabled when a valid project
    /// folder IS selected. Otherwise the fix would overcorrect.
    /// </summary>
    [AvaloniaFact]
    public void OpenNewTaskDialogCommand_Enabled_WhenRootPathExists()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };

        Assert.True(Directory.Exists(viewModel.RootPath),
            "Sanity: the test repo root must exist on disk.");

        Assert.True(viewModel.OpenNewTaskDialogCommand.CanExecute(null),
            "OpenNewTaskDialogCommand must be enabled when RootPath is a valid " +
            "existing directory.");
    }

    /// <summary>
    /// "Create task" button must be disabled when no project is selected,
    /// even if the user has typed a valid title. The guard must check both
    /// title validity AND Directory.Exists(RootPath).
    /// </summary>
    [AvaloniaFact]
    public void CreateNewTaskCommand_Disabled_WhenRootPathIsEmpty_EvenWithValidTitle()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.OpenNewTaskDialogCommand.Execute(null);

        // Type a valid title — with a project this would enable Create.
        viewModel.NewTaskTitle = "Implement feature X";

        Assert.False(viewModel.CreateNewTaskCommand.CanExecute(null),
            "CreateNewTaskCommand must be disabled when RootPath is empty, " +
            "even when a valid title is entered. The CanCreateNewTask guard " +
            "must also check Directory.Exists(RootPath).");
    }

    /// <summary>
    /// "Create task" button must be disabled when RootPath does not exist
    /// on disk, even with a valid title typed in.
    /// </summary>
    [AvaloniaFact]
    public void CreateNewTaskCommand_Disabled_WhenRootPathDoesNotExist_EvenWithValidTitle()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var viewModel = new MainWindowViewModel { RootPath = nonExistent };
        viewModel.OpenNewTaskDialogCommand.Execute(null);

        viewModel.NewTaskTitle = "Implement feature X";

        Assert.False(viewModel.CreateNewTaskCommand.CanExecute(null),
            "CreateNewTaskCommand must be disabled when RootPath does not exist, " +
            "even with a valid title.");
    }

    /// <summary>
    /// Regression: "Create task" must be enabled when both RootPath is valid
    /// AND a title has been entered.
    /// </summary>
    [AvaloniaFact]
    public void CreateNewTaskCommand_Enabled_WhenRootPathExists_AndTitleValid()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        viewModel.OpenNewTaskDialogCommand.Execute(null);

        viewModel.NewTaskTitle = "Implement feature X";

        Assert.True(viewModel.CreateNewTaskCommand.CanExecute(null),
            "CreateNewTaskCommand must be enabled when RootPath is valid AND " +
            "a title is entered.");
    }

    /// <summary>
    /// Changing RootPath from empty to a valid directory must notify
    /// OpenNewTaskDialogCommand's CanExecuteChanged so the bound "New"
    /// button re-evaluates and becomes enabled.
    /// </summary>
    [AvaloniaFact]
    public void ChangingRootPath_NotifiesOpenNewTaskDialogCommand_CanExecuteChanged()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var viewModel = new MainWindowViewModel();
        var changedCount = 0;
        viewModel.OpenNewTaskDialogCommand.CanExecuteChanged += (_, _) => changedCount++;

        Assert.Equal(0, changedCount);

        viewModel.RootPath = repo.Root;

        Assert.True(changedCount >= 1,
            "CanExecuteChanged must fire on OpenNewTaskDialogCommand when " +
            "RootPath changes (missing [NotifyCanExecuteChangedFor(nameof(OpenNewTaskDialogCommand))]).");
    }

    /// <summary>
    /// Changing RootPath from empty to a valid directory must notify
    /// CreateNewTaskCommand's CanExecuteChanged so the bound "Create task"
    /// button re-evaluates. When the user opens the dialog before selecting
    /// a project, then selects a project, the button should wake up.
    /// </summary>
    [AvaloniaFact]
    public void ChangingRootPath_NotifiesCreateNewTaskCommand_CanExecuteChanged()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var viewModel = new MainWindowViewModel();
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        viewModel.NewTaskTitle = "Implement feature X";

        var changedCount = 0;
        viewModel.CreateNewTaskCommand.CanExecuteChanged += (_, _) => changedCount++;

        viewModel.RootPath = repo.Root;

        Assert.True(changedCount >= 1,
            "CanExecuteChanged must fire on CreateNewTaskCommand when " +
            "RootPath changes (missing [NotifyCanExecuteChangedFor(nameof(CreateNewTaskCommand))]).");
    }
}
