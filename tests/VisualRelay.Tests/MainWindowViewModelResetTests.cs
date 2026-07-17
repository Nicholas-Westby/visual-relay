using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed class MainWindowViewModelResetTests
{
    [AvaloniaFact]
    public async Task ResetFlaggedTask_ArchivesRunDir_AndShowsPending()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged-task", "# Flagged Task\n\nNeeds work.");
        var runDir = Path.Combine(repo.Root, ".relay", "flagged-task");
        Directory.CreateDirectory(runDir);
        repo.WriteNeedsReview("flagged-task", "stage 5 verify failed");
        File.WriteAllText(Path.Combine(runDir, "flagged-work.bundle"), "bundled work content");
        File.WriteAllText(Path.Combine(runDir, "status.json"), "[{\"stage\":1,\"status\":\"Done\"}]");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await vm.LoadInitialAsync();
        var task = vm.Tasks.Single(t => t.Id == "flagged-task");
        Assert.True(task.NeedsReview);
        Assert.Equal("Needs review", task.StateLabel);
        vm.SelectedTask = task;
        Dispatcher.UIThread.RunJobs();
        await (vm.LastSelectionLoad ?? Task.CompletedTask);
        Assert.True(vm.ResetSelectedTaskCommand.CanExecute(null));
        await vm.ResetSelectedTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        task = vm.Tasks.Single(t => t.Id == "flagged-task");
        Assert.False(task.NeedsReview);
        Assert.Equal("Pending", task.StateLabel);
        Assert.False(Directory.Exists(runDir));
        var archives = Directory.GetDirectories(Path.Combine(repo.Root, ".relay"), "flagged-task.reset-*");
        Assert.Single(archives);
        Assert.True(File.Exists(Path.Combine(archives[0], "flagged-work.bundle")));
        Assert.True(File.Exists(Path.Combine(archives[0], "status.json")));
        Assert.True(File.Exists(Path.Combine(archives[0], "NEEDS-REVIEW")));
    }
    [AvaloniaFact]
    public async Task CanReset_FalseWhenNotFlagged()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("pending-task", "# Pending Task\n");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await vm.LoadInitialAsync();
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "pending-task");
        Dispatcher.UIThread.RunJobs();
        await (vm.LastSelectionLoad ?? Task.CompletedTask);
        Assert.False(vm.SelectedTask!.NeedsReview);
        Assert.False(vm.ResetSelectedTaskCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public async Task CanReset_FalseWhenShowArchive()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged-task", "# Flagged Task\n");
        repo.WriteNeedsReview("flagged-task", "stage 5 verify failed");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await vm.LoadInitialAsync();
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "flagged-task");
        Dispatcher.UIThread.RunJobs();
        await (vm.LastSelectionLoad ?? Task.CompletedTask);
        Assert.True(vm.ResetSelectedTaskCommand.CanExecute(null));
        vm.ShowArchive = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ResetSelectedTaskCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public async Task CanReset_TrueEvenWhenIsBusy()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged-task", "# Flagged Task\n");
        repo.WriteNeedsReview("flagged-task", "stage 5 verify failed");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await vm.LoadInitialAsync();
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "flagged-task");
        Dispatcher.UIThread.RunJobs();
        await (vm.LastSelectionLoad ?? Task.CompletedTask);
        Assert.True(vm.ResetSelectedTaskCommand.CanExecute(null));
        vm.IsBusy = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ResetSelectedTaskCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public async Task CanReset_TrueEvenWhenAnotherTaskIsRunning()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged-task", "# Flagged Task\n");
        repo.WriteNestedTask("running-task", "# Running Task\n");
        repo.WriteNeedsReview("flagged-task", "stage 5 verify failed");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await vm.LoadInitialAsync();
        vm.CreateDrainLifecycleCallbacks().OnExecuteStarted?.Invoke("running-task");
        Dispatcher.UIThread.RunJobs();
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "flagged-task");
        Dispatcher.UIThread.RunJobs();
        await (vm.LastSelectionLoad ?? Task.CompletedTask);
        Assert.True(vm.ResetSelectedTaskCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public void CanReset_FalseWhenNoTaskSelected()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        Assert.Null(vm.SelectedTask);
        Assert.False(vm.ResetSelectedTaskCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public async Task Reset_HumanGui_ShowsConfirmation_AndHonorsCancel()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged-task", "# Flagged Task\n");
        repo.WriteNeedsReview("flagged-task", "stage 5 verify failed");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await vm.LoadInitialAsync();
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "flagged-task");
        Dispatcher.UIThread.RunJobs();
        await (vm.LastSelectionLoad ?? Task.CompletedTask);
        var dialogShown = false;
        vm.ShowConfirmationAsync = (_, _, _) => { dialogShown = true; return Task.FromResult(false); };
        await vm.ResetSelectedTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(dialogShown);
        Assert.True(vm.Tasks.Single(t => t.Id == "flagged-task").NeedsReview);
    }
    [AvaloniaFact]
    public async Task Reset_HumanGui_ConfirmsAndResets()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged-task", "# Flagged Task\n");
        repo.WriteNeedsReview("flagged-task", "stage 5 verify failed");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await vm.LoadInitialAsync();
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "flagged-task");
        Dispatcher.UIThread.RunJobs();
        await (vm.LastSelectionLoad ?? Task.CompletedTask);
        var dialogShown = false;
        vm.ShowConfirmationAsync = (t, m, l) =>
        {
            dialogShown = true;
            Assert.Contains("Reset", t);
            Assert.Contains("flagged-task", m);
            Assert.Equal("Reset", l);
            return Task.FromResult(true);
        };
        await vm.ResetSelectedTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(dialogShown);
        Assert.False(vm.Tasks.Single(t => t.Id == "flagged-task").NeedsReview);
    }
    [AvaloniaFact]
    public async Task Reset_ViaApi_WithoutConfirm_Returns409()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged-task", "# Flagged Task\n");
        repo.WriteNeedsReview("flagged-task", "stage 5 verify failed");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await vm.LoadInitialAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
            vm.SelectedTask = vm.Tasks.Single(t => t.Id == "flagged-task"));
        Dispatcher.UIThread.RunJobs();
        await (vm.LastSelectionLoad ?? Task.CompletedTask);
        var dialogShown = false;
        vm.ShowConfirmationAsync = (_, _, _) => { dialogShown = true; return Task.FromResult(true); };
        var api = new ControlApi(vm, new Window { DataContext = vm });
        var (status, json) = await api.InvokeCommandAsync("reset-selected", null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(409, status);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("confirmation required", doc.RootElement.GetProperty("error").GetString());
        Assert.False(dialogShown);
        Assert.True(vm.Tasks.Single(t => t.Id == "flagged-task").NeedsReview);
    }
    [AvaloniaFact]
    public async Task Reset_ViaApi_WithConfirm_CompletesAndResets_WithoutDialog()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged-task", "# Flagged Task\n");
        repo.WriteNeedsReview("flagged-task", "stage 5 verify failed");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root, ShowConfirmationAsync = null };
        await vm.LoadInitialAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
            vm.SelectedTask = vm.Tasks.Single(t => t.Id == "flagged-task"));
        Dispatcher.UIThread.RunJobs();
        await (vm.LastSelectionLoad ?? Task.CompletedTask);
        var dialogShown = false;
        vm.ShowConfirmationAsync = (_, _, _) => { dialogShown = true; return Task.FromResult(true); };
        var api = new ControlApi(vm, new Window { DataContext = vm });
        var (status, json) = await api.InvokeCommandAsync("reset-selected", "{\"confirm\":true}");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(dialogShown);
        Assert.False(vm.Tasks.Single(t => t.Id == "flagged-task").NeedsReview);
    }
    [AvaloniaFact]
    public async Task Reset_DuringActiveSequentialDrain_JoinsAtNextBoundary()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("task-a", "# Task A\n");
        repo.WriteNestedTask("task-b", "# Task B\n");
        var runDir = Path.Combine(repo.Root, ".relay", "task-b");
        Directory.CreateDirectory(runDir);
        repo.WriteNeedsReview("task-b", "stage 5 verify failed");
        File.WriteAllText(Path.Combine(runDir, "status.json"), "[{\"stage\":1,\"status\":\"Done\"}]");
        File.WriteAllText(Path.Combine(runDir, "flagged-work.bundle"), "bundled");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();
        Assert.True(controller.Tasks.Single(t => t.Id == "task-b").NeedsReview);
        // After task-a completes: reset task-b mid-drain, evict from seen set.
        // Drain loop processes in-queue task-b next; run dir gone → stage 1 restart.
        runner.AfterRun = () =>
        {
            if (runner.TasksRun is ["task-a"])
            {
                new RelayTaskRepository(repo.Root).ResetTask("task-b");
                controller.RemoveFromSeen("task-b");
            }
        };
        await controller.DrainAsync(mode: RunAllMode.Sequential);
        Assert.Equal(["task-a", "task-b"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        var archives = Directory.GetDirectories(Path.Combine(repo.Root, ".relay"), "task-b.reset-*");
        Assert.Single(archives);
        Assert.True(File.Exists(Path.Combine(archives[0], "flagged-work.bundle")));
        Assert.False(Directory.Exists(runDir));
    }
    [AvaloniaFact]
    public void RemoveFromSeen_DoesNotThrow_WhenNoDrainActive()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var controller = new RelayQueueController(repo.Root, new NoopTaskRunner());
        controller.RemoveFromSeen("nonexistent");
    }
    [AvaloniaFact]
    public async Task ArchiveDirectory_InvisibleToListing()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("archive-test", "# Archive Test\n");
        repo.WriteNeedsReview("archive-test", "stage 5 verify failed");
        var archiveDir = Path.Combine(repo.Root, ".relay", "archive-test.reset-20260101T120000");
        Directory.CreateDirectory(archiveDir);
        File.WriteAllText(Path.Combine(archiveDir, "NEEDS-REVIEW"), "old reason\n");
        var repository = new RelayTaskRepository(repo.Root);
        var tasks = await repository.ListAsync();
        var matching = tasks.Where(t => t.Id == "archive-test").ToArray();
        Assert.Single(matching);
        Assert.True(matching[0].NeedsReview);
        repository.ResetTask("archive-test");
        tasks = await repository.ListAsync();
        matching = tasks.Where(t => t.Id == "archive-test").ToArray();
        Assert.Single(matching);
        Assert.False(matching[0].NeedsReview);
        Assert.Equal("Pending", matching[0].StateLabel);
        var archiveDir2 = Path.Combine(repo.Root, ".relay", "archive-test.reset-20260102T120000");
        Directory.CreateDirectory(archiveDir2);
        File.WriteAllText(Path.Combine(archiveDir2, "NEEDS-REVIEW"), "another reason\n");
        tasks = await repository.ListAsync();
        matching = tasks.Where(t => t.Id == "archive-test").ToArray();
        Assert.Single(matching);
        Assert.False(matching[0].NeedsReview);
    }

    private sealed class NoopTaskRunner : IRelayTaskRunner
    {
        public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, "hash", "commit", null));
    }
}
