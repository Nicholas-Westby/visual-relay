using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Drain-lifecycle status + roster reconciliation. During a Run All drain the
/// bottom-left status text must follow the EXECUTING task (not the last concurrent-
/// planning message), and a committed+archived task (spec moved to completed/) must
/// leave the active roster instead of lingering as "Pending" with a stale MarkdownPath
/// that a later re-read throws on. Drives the internal CreateDrainLifecycleCallbacks
/// hooks directly (no real swival run), mirroring LiveStateViewModelTests.
/// </summary>
public sealed class DrainLifecycleStatusTests
{
    [Fact]
    public async Task OnExecuteStarted_SetsRunningStatusForExecutingTask()
    {
        // Bug: the status stayed on the last concurrent-planning message
        // ("Planning <last task>...") for the whole execute phase, because
        // OnExecuteStarted never set StatusText.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task-a", "# A\n");
        repo.WriteTask("task-z", "# Z\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        var callbacks = viewModel.CreateDrainLifecycleCallbacks();
        callbacks.OnPlanningStarted!("task-a");
        callbacks.OnPlanningStarted!("task-z"); // last planning task wins the status text
        Assert.Equal("Planning task-z…", viewModel.StatusText);

        // Execution begins for task-a (an EARLIER task): the status must follow it.
        callbacks.OnExecuteStarted!("task-a");

        Assert.Equal("Running task-a", viewModel.StatusText);
    }

    [Fact]
    public async Task OnExecuteCompleted_ArchivedTaskLeavesRosterAndRepointsSelection()
    {
        // Bug: a committed task whose spec moved to completed/ lingered as "Pending"
        // and its stale MarkdownPath was re-read -> "Could not find a part of the path".
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("first", "# First\n");
        repo.WriteTask("second", "# Second\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "first");

        var callbacks = viewModel.CreateDrainLifecycleCallbacks();
        callbacks.OnExecuteStarted!("first");

        // Simulate archiveOnDone moving the committed task's spec out of the active tree.
        var firstRow = viewModel.Tasks.Single(t => t.Id == "first");
        File.Delete(firstRow.MarkdownPath);

        callbacks.OnExecuteCompleted!("first",
            new RelayTaskOutcome("first", RelayTaskOutcomeStatus.Committed, null, "sha", null));

        // The archived task left the active roster (no longer "Pending")...
        Assert.DoesNotContain(viewModel.Tasks, t => t.Id == "first");
        // ...and the selection re-pointed off the gone task (nothing re-reads its path).
        Assert.NotEqual("first", viewModel.SelectedTask?.Id);
    }
}
