using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Regression guard for the stale "LATEST RUN FAILED" banner. When the
/// currently-selected task is re-run (single-run OR queue drain), the displayed
/// <see cref="MainWindowViewModel.SelectedTaskError"/> must track the CURRENT
/// run: cleared the instant the run starts, and on completion reflect the new
/// outcome (flagged → new reason, committed → cleared). Previously the drain
/// run-start path never touched the on-screen error, so a committed/re-running
/// task kept showing the prior run's failure.
///
/// Drives the exact drain lifecycle seam (<c>CreateDrainLifecycleCallbacks</c>)
/// rather than a real swival/relay run, so no process is spawned.
/// </summary>
[Collection("Headless")]
public sealed class TaskDetailErrorRefreshTests
{
    // async (awaited, never blocked) so these run cleanly under the single-threaded
    // Avalonia headless dispatcher — a sync-over-async .GetAwaiter().GetResult()
    // here would deadlock the dispatcher.
    private static async Task WriteFlaggedStatusAsync(string root, string taskId, int stage, string error)
    {
        var taskDirectory = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        var entries = new[] { new StageStatusEntry(stage, $"Stage {stage}", "Flagged", Error: error) };
        await StageStatusRecord.WriteAsync(taskDirectory, entries);
    }

    private static async Task WriteCommittedStatusAsync(string root, string taskId)
    {
        var taskDirectory = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        var entries = RelayStages.All
            .Select(s => new StageStatusEntry(s.Number, s.Name, "Done"))
            .ToArray();
        await StageStatusRecord.WriteAsync(taskDirectory, entries);
    }

    [AvaloniaFact]
    public async Task DrainRunStart_OnSelectedTask_ClearsStaleError_ThenCommitKeepsItCleared()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("broken", "# Broken\n");
        await WriteFlaggedStatusAsync(repo.Root, "broken", 1, "verify failed after 5 fix-verify attempts");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Select the failed task — the stale error banner is showing.
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "broken");
        await viewModel.LastSelectionLoad!;
        Assert.True(viewModel.HasSelectedTaskError);
        Assert.Equal("verify failed after 5 fix-verify attempts", viewModel.SelectedTaskError);

        var lifecycle = viewModel.CreateDrainLifecycleCallbacks();
        Assert.NotNull(lifecycle.OnExecuteStarted);
        Assert.NotNull(lifecycle.OnExecuteCompleted);

        // The drain reaches this (selected) task: run-start must clear the stale error.
        lifecycle.OnExecuteStarted.Invoke("broken");
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.HasSelectedTaskError);
        Assert.True(string.IsNullOrEmpty(viewModel.SelectedTaskError));

        // The re-run COMMITS (no flagged entry left) — the error stays cleared.
        await WriteCommittedStatusAsync(repo.Root, "broken");
        lifecycle.OnExecuteCompleted.Invoke("broken",
            new RelayTaskOutcome("broken", RelayTaskOutcomeStatus.Committed, null, "sha", null));
        await viewModel.LastRunCompletionRefresh!;
        Assert.False(viewModel.HasSelectedTaskError);
        Assert.True(string.IsNullOrEmpty(viewModel.SelectedTaskError));
    }

    [AvaloniaFact]
    public async Task DrainRun_OnSelectedTask_FlagsWithNewError_SurfacesNewReason()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("broken", "# Broken\n");
        await WriteFlaggedStatusAsync(repo.Root, "broken", 1, "old failure");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "broken");
        await viewModel.LastSelectionLoad!;
        Assert.Equal("old failure", viewModel.SelectedTaskError);

        var lifecycle = viewModel.CreateDrainLifecycleCallbacks();
        Assert.NotNull(lifecycle.OnExecuteStarted);
        Assert.NotNull(lifecycle.OnExecuteCompleted);

        // Run starts → stale error cleared.
        lifecycle.OnExecuteStarted.Invoke("broken");
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.HasSelectedTaskError);

        // The re-run FLAGS with a NEW reason — the new error must surface.
        await WriteFlaggedStatusAsync(repo.Root, "broken", 4, "new distinct failure");
        lifecycle.OnExecuteCompleted.Invoke("broken",
            new RelayTaskOutcome("broken", RelayTaskOutcomeStatus.Flagged, null, null, "new distinct failure"));
        await viewModel.LastRunCompletionRefresh!;
        Assert.True(viewModel.HasSelectedTaskError);
        Assert.Equal("new distinct failure", viewModel.SelectedTaskError);
    }

    [AvaloniaFact]
    public async Task DrainRunStart_OnDifferentTask_LeavesSelectedTaskErrorIntact()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("broken", "# Broken\n");
        repo.WriteTask("other", "# Other\n");
        await WriteFlaggedStatusAsync(repo.Root, "broken", 1, "broken's error");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "broken");
        await viewModel.LastSelectionLoad!;
        Assert.Equal("broken's error", viewModel.SelectedTaskError);

        var lifecycle = viewModel.CreateDrainLifecycleCallbacks();
        Assert.NotNull(lifecycle.OnExecuteStarted);

        // A DIFFERENT task starts running — the viewed task's error must stay put.
        lifecycle.OnExecuteStarted.Invoke("other");
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.HasSelectedTaskError);
        Assert.Equal("broken's error", viewModel.SelectedTaskError);
    }
}
