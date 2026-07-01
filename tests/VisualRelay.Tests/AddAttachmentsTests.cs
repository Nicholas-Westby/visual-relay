using System.Reflection;
using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed class AddAttachmentsTests
{
    /// <summary>
    /// Selecting a task enables AddAttachments, archive disables it, and the
    /// running-task-ID-scoped check disables it only for the running/rewriting task
    /// — not for unrelated tasks.
    /// </summary>
    [AvaloniaFact]
    public async Task SelectTask_EnablesAddAttachments_ArchiveAndRunningTaskDisableIt()
    {
        // ── Arrange: ViewModel with two tasks, no archive ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task-a", "# Task A\n");
        repo.WriteTask("task-b", "# Task B\n");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };

        // ── Before load: no task selected → command disabled ──
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments should be disabled before any task is loaded.");

        await viewModel.LoadInitialAsync();
        Assert.False(viewModel.NeedsInitialization);

        // ── After load a task is auto-selected → command enabled ──
        Assert.NotNull(viewModel.SelectedTask);
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be enabled when a task is selected.");

        // ── Clear selection → disabled ──
        viewModel.SelectedTask = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments should be disabled when no task is selected.");

        // ── Re-select → enabled ──
        viewModel.SelectedTask = viewModel.Tasks[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be enabled when a task is selected.");

        // ── Switch to archive view → disabled ──
        viewModel.ShowArchive = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be disabled when viewing archive.");

        // ── Return to queue view → enabled ──
        viewModel.ShowArchive = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be re-enabled when leaving archive view.");

        // ── Begin running the SELECTED task → disabled ──
        var beginRunning = GetBeginRunningTaskMethod();
        var clearRunning = GetClearRunningTaskMethod();
        var selectedTask = viewModel.SelectedTask!;
        beginRunning.Invoke(viewModel, [selectedTask]);
        selectedTask.MarkPlanning();
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be disabled when the selected task is running.");

        // ── An UNRELATED task starts running → selected task still not
        //     running, so AddAttachments stays enabled ──
        var otherTask = viewModel.Tasks.First(t => t.Id == "task-b");
        // First clear the selected task's run so it's isolated.
        clearRunning.Invoke(viewModel, [selectedTask.Id]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be re-enabled when selected task stops running.");

        // Now start the other task — this should NOT disable AddAttachments.
        beginRunning.Invoke(viewModel, [otherTask]);
        otherTask.MarkPlanning();
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must stay enabled while an unrelated task is running.");

        // Clean up.
        clearRunning.Invoke(viewModel, [otherTask.Id]);
        Dispatcher.UIThread.RunJobs();

        // ── Begin rewriting the SELECTED task → disabled ──
        RewritingTaskIds(viewModel).Add(selectedTask.Id);
        RaiseRewriteStateChanged(viewModel);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be disabled when the selected task is being rewritten.");

        // ── Clear rewrite → enabled ──
        RewritingTaskIds(viewModel).Remove(selectedTask.Id);
        RaiseRewriteStateChanged(viewModel);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must be re-enabled when rewrite completes.");
    }

    /// <summary>
    /// Verifies that CanExecuteChanged fires when SelectedTask, ShowArchive,
    /// running-task state, and rewrite state change — not IsBusy.
    /// </summary>
    [AvaloniaFact]
    public async Task ChangingSelectedTask_NotifiesCanExecuteChanged()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("one", "# One\n");
        repo.WriteTask("two", "# Two\n");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };

        var changedCount = 0;
        viewModel.AddAttachmentsCommand.CanExecuteChanged += (_, _) => changedCount++;

        // ── Load — auto-selects a task ──
        await viewModel.LoadInitialAsync();
        var afterLoad = changedCount;

        // ── Clear selection — must fire ──
        viewModel.SelectedTask = null;
        Dispatcher.UIThread.RunJobs();
        Assert.True(changedCount > afterLoad);

        // ── Select a task — must fire again ──
        var afterClear = changedCount;
        viewModel.SelectedTask = viewModel.Tasks[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(changedCount > afterClear);

        // ── Toggle ShowArchive — must fire ──
        var beforeArchive = changedCount;
        viewModel.ShowArchive = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(changedCount > beforeArchive);

        var beforeUnarchive = changedCount;
        viewModel.ShowArchive = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(changedCount > beforeUnarchive);

        // ── Begin running the selected task — must fire ──
        var beginRunning = GetBeginRunningTaskMethod();
        var clearRunning = GetClearRunningTaskMethod();
        var beforeRun = changedCount;
        beginRunning.Invoke(viewModel, [viewModel.SelectedTask!]);
        viewModel.SelectedTask!.MarkPlanning();
        Dispatcher.UIThread.RunJobs();
        Assert.True(changedCount > beforeRun);

        // ── Clear running — must fire ──
        var beforeClear = changedCount;
        clearRunning.Invoke(viewModel, [viewModel.SelectedTask!.Id]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(changedCount > beforeClear);

        // ── Begin rewrite — must fire ──
        var beforeRewrite = changedCount;
        RewritingTaskIds(viewModel).Add(viewModel.SelectedTask!.Id);
        RaiseRewriteStateChanged(viewModel);
        Dispatcher.UIThread.RunJobs();
        Assert.True(changedCount > beforeRewrite);

        // ── Clear rewrite — must fire ──
        var beforeRewriteClear = changedCount;
        RewritingTaskIds(viewModel).Remove(viewModel.SelectedTask!.Id);
        RaiseRewriteStateChanged(viewModel);
        Dispatcher.UIThread.RunJobs();
        Assert.True(changedCount > beforeRewriteClear);
    }

    /// <summary>
    /// No selection, archive mode, running-task state, or rewrite state each
    /// independently gate the command regardless of the other preconditions.
    /// </summary>
    [AvaloniaFact]
    public async Task AddAttachments_GatedBySelection_Archive_AndRunningTaskId()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Clear the auto-selected task → disabled.
        viewModel.SelectedTask = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));

        // Select a task → enabled (not archive, not running, not rewriting).
        viewModel.SelectedTask = viewModel.Tasks.First(t => t.Id == "alpha");
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null));

        // Archive mode → disabled even with a task selected.
        viewModel.ShowArchive = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));

        // Leave archive → enabled again.
        viewModel.ShowArchive = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null));

        // ── Selected task running → disabled ──
        var beginRunning = GetBeginRunningTaskMethod();
        var clearRunning = GetClearRunningTaskMethod();
        beginRunning.Invoke(viewModel, [viewModel.SelectedTask!]);
        viewModel.SelectedTask!.MarkPlanning();
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));

        // ── Selected task stops running → enabled ──
        clearRunning.Invoke(viewModel, [viewModel.SelectedTask.Id]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null));

        // ── Unrelated task running → still enabled ──
        var otherTask = viewModel.Tasks.First(t => t.Id == "beta");
        beginRunning.Invoke(viewModel, [otherTask]);
        otherTask.MarkPlanning();
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachments must stay enabled while an unrelated task runs.");

        clearRunning.Invoke(viewModel, [otherTask.Id]);
        Dispatcher.UIThread.RunJobs();

        // ── Selected task rewriting → disabled ──
        RewritingTaskIds(viewModel).Add(viewModel.SelectedTask.Id);
        RaiseRewriteStateChanged(viewModel);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));

        // ── Rewrite done → enabled ──
        RewritingTaskIds(viewModel).Remove(viewModel.SelectedTask.Id);
        RaiseRewriteStateChanged(viewModel);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null));

        // Clear selection → disabled even though not archive, not running, not rewriting.
        viewModel.SelectedTask = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));
    }

    /// <summary>
    /// Executing AddAttachmentsAsync in a headless context (NullFilePicker
    /// returns an empty list) safely early-returns without side effects.
    /// This guards against regressions where the command body might throw
    /// when the picker is absent.
    /// </summary>
    [AvaloniaFact]
    public async Task AddAttachmentsAsync_Headless_NoOps_WithoutCrash()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task", "# Task\n");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };

        // Sanity: before load, no task is selected → disabled.
        Assert.False(viewModel.AddAttachmentsCommand.CanExecute(null));

        await viewModel.LoadInitialAsync();

        // Ensure a task is selected so the command can execute.
        viewModel.SelectedTask = viewModel.Tasks[0];
        Assert.True(viewModel.AddAttachmentsCommand.CanExecute(null),
            "AddAttachmentsCommand must be executable after selecting a task.");

        // Execute — NullFilePicker returns empty, so this is a no-op.
        await viewModel.AddAttachmentsCommand.ExecuteAsync(null);

        // Task should still be selected and unchanged.
        Assert.NotNull(viewModel.SelectedTask);
        Assert.Equal("task", viewModel.SelectedTask.Id);
    }

    // ── Reflection helpers for private members ──────────────────────────
    private static MethodInfo GetBeginRunningTaskMethod() =>
        typeof(MainWindowViewModel).GetMethod("BeginRunningTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static MethodInfo GetClearRunningTaskMethod() =>
        typeof(MainWindowViewModel).GetMethod("ClearRunningTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static HashSet<string> RewritingTaskIds(MainWindowViewModel vm) =>
        (HashSet<string>)typeof(MainWindowViewModel)
            .GetField("_rewritingTaskIds", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
    private static void RaiseRewriteStateChanged(MainWindowViewModel vm) =>
        typeof(MainWindowViewModel).GetMethod("RaiseRewriteStateChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
}
