using Avalonia.Headless;
using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed class NewTaskAuthoringTests
{
    /// <summary>
    /// Headless UI/view-model regression test for the previously-broken path:
    /// opening new-task authoring, setting a title, asserting the Create
    /// command becomes executable, creating the task, and verifying the result.
    /// The bug was that [NotifyCanExecuteChangedFor] was missing on NewTaskTitle
    /// and IsBusy, so the Create button never enabled when the user typed.
    /// </summary>
    [Fact]
    public async Task OpenNewTaskDialog_SetTitle_EnablesCreateCommand_AndCreatesTask()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

        // ViewModel setup can happen outside Dispatch since it doesn't
        // require a live visual tree.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("existing", "# Existing\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        Assert.False(viewModel.NeedsInitialization);

        // ── Open the new-task authoring view ──
        // Use the non-async Dispatch overload so exceptions propagate
        // (the async overload silently swallows XunitExceptions).
        await session.Dispatch(() =>
        {
            viewModel.OpenNewTaskDialogCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }, CancellationToken.None);

        Assert.True(viewModel.IsNewTaskDialogOpen);

        // ── Title is empty → Create must be disabled ──
        Assert.False(viewModel.CreateNewTaskCommand.CanExecute(null),
            "Create should be disabled when title is empty.");

        // ── Type a title — this is the previously-broken path:
        //      CanExecute was never re-evaluated because
        //      [NotifyCanExecuteChangedFor] was missing. ──
        viewModel.NewTaskTitle = "Implement feature X";

        // ── Assert: Create is now enabled ──
        Assert.True(viewModel.CreateNewTaskCommand.CanExecute(null),
            "CreateNewTaskCommand must become executable after typing a title. " +
            "Missing [NotifyCanExecuteChangedFor(nameof(CreateNewTaskCommand))] " +
            "on NewTaskTitle or IsBusy.");

        // ── Act: create the task ──
        await viewModel.CreateNewTaskCommand.ExecuteAsync(null);

        // ── Assert: dialog closed ──
        Assert.False(viewModel.IsNewTaskDialogOpen);

        // ── Assert: task file exists on disk ──
        var expectedPath = Path.Combine(repo.Root, "llm-tasks",
            "implement-feature-x.md");
        Assert.True(File.Exists(expectedPath),
            $"Expected task file at {expectedPath}");

        // ── Assert: new task is selected in the queue ──
        Assert.NotNull(viewModel.SelectedTask);
        Assert.Equal("implement-feature-x", viewModel.SelectedTask.Id);
    }

    /// <summary>
    /// Verifies that CanExecuteChanged fires when NewTaskTitle and IsBusy
    /// change — the root cause of the dead Create task button.
    /// Without [NotifyCanExecuteChangedFor] the event never fires, so the
    /// Button never re-queries CanExecute.
    /// </summary>
    [Fact]
    public void ChangingNewTaskTitle_NotifiesCanExecuteChanged()
    {
        var viewModel = new MainWindowViewModel();
        var changedCount = 0;
        viewModel.CreateNewTaskCommand.CanExecuteChanged += (_, _) => changedCount++;

        // Initial state — no notification yet.
        Assert.Equal(0, changedCount);

        // Setting an empty/whitespace title should still notify.
        viewModel.NewTaskTitle = "   ";
        Assert.True(changedCount >= 1,
            "CanExecuteChanged must fire when NewTaskTitle changes " +
            "(missing [NotifyCanExecuteChangedFor(nameof(CreateNewTaskCommand))]).");

        // Setting a real title notifies again.
        var afterTitle = changedCount;
        viewModel.NewTaskTitle = "Fix the bug";
        Assert.True(changedCount > afterTitle,
            "CanExecuteChanged must fire on subsequent title changes.");

        // Setting IsBusy must fire.
        var beforeBusy = changedCount;
        viewModel.IsBusy = true;
        Assert.True(changedCount > beforeBusy,
            "CanExecuteChanged must fire when IsBusy changes.");

        // Setting IsBusy back to false must fire.
        var beforeUnbusy = changedCount;
        viewModel.IsBusy = false;
        Assert.True(changedCount > beforeUnbusy,
            "CanExecuteChanged must fire when IsBusy returns to false.");
    }

    /// <summary>
    /// Whitespace or empty titles keep Create disabled; IsBusy=true also
    /// gates the command regardless of title content.
    /// </summary>
    [Fact]
    public void NewTaskTitle_WhitespaceOrEmpty_KeepsCreateDisabled_BusyAlsoDisables()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        // Empty title → disabled.
        viewModel.NewTaskTitle = string.Empty;
        Assert.False(viewModel.CreateNewTaskCommand.CanExecute(null));

        // Whitespace-only title → disabled.
        viewModel.NewTaskTitle = "   ";
        Assert.False(viewModel.CreateNewTaskCommand.CanExecute(null));

        // Valid title → enabled.
        viewModel.NewTaskTitle = "Fix the bug";
        Assert.True(viewModel.CreateNewTaskCommand.CanExecute(null));

        // Valid title but busy → disabled.
        viewModel.IsBusy = true;
        Assert.False(viewModel.CreateNewTaskCommand.CanExecute(null));

        // No longer busy → enabled again.
        viewModel.IsBusy = false;
        Assert.True(viewModel.CreateNewTaskCommand.CanExecute(null));
    }

    /// <summary>
    /// Opening new-task authoring while editing an existing task (and vice
    /// versa) must not show both editors at once — entering one mode exits
    /// the other.
    /// </summary>
    [Fact]
    public async Task OpenNewTaskDialog_ExitsEditMode_AndEditExitsNewTaskMode()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("existing", "# Existing\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Set up a selected task so EditSelectedTask can execute.
        viewModel.SelectedTask = viewModel.Tasks[0];
        Assert.NotNull(viewModel.SelectedTask);

        // ── Enter edit mode ──
        viewModel.EditSelectedTaskCommand.Execute(null);
        Assert.True(viewModel.IsEditingMarkdown);
        Assert.False(string.IsNullOrEmpty(viewModel.EditBuffer));

        // ── Open new-task dialog → must exit edit mode ──
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        Assert.True(viewModel.IsNewTaskDialogOpen,
            "New-task dialog should be open after OpenNewTaskDialog.");
        Assert.False(viewModel.IsEditingMarkdown,
            "Edit mode must exit when new-task authoring opens.");

        // ── Now enter edit mode again → must exit new-task mode ──
        viewModel.EditSelectedTaskCommand.Execute(null);
        Assert.True(viewModel.IsEditingMarkdown,
            "Edit mode should be active after EditSelectedTask.");
        Assert.False(viewModel.IsNewTaskDialogOpen,
            "New-task dialog must close when edit mode is entered.");
    }

    /// <summary>
    /// Canceling new-task authoring clears the in-progress title, body, and
    /// error, closes the authoring view, and restores the read-only state.
    /// </summary>
    [Fact]
    public void CancelNewTaskDialog_ClearsFieldsAndCloses()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        // Open the dialog and fill in fields.
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        Assert.True(viewModel.IsNewTaskDialogOpen);

        viewModel.NewTaskTitle = "Draft title";
        viewModel.NewTaskBody = "# Draft body\n\nSome content.";
        viewModel.NewTaskError = "previous error";
        Assert.Equal("Draft title", viewModel.NewTaskTitle);
        Assert.Equal("# Draft body\n\nSome content.", viewModel.NewTaskBody);
        Assert.Equal("previous error", viewModel.NewTaskError);

        // ── Cancel (second invocation of OpenNewTaskDialog toggles it off) ──
        viewModel.OpenNewTaskDialogCommand.Execute(null);

        // ── Assert: dialog closed and all fields cleared ──
        Assert.False(viewModel.IsNewTaskDialogOpen);
        Assert.Equal(string.Empty, viewModel.NewTaskTitle);
        Assert.Equal(string.Empty, viewModel.NewTaskBody);
        Assert.Null(viewModel.NewTaskError);
    }
}
