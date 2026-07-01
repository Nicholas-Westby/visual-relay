using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// Execution-flow tests for the "Create task to fix" command: completion
/// modal, error path, headless auto-resolve, and busy-state transitions.
/// </summary>
public sealed partial class MainWindowViewModelFixTaskTests
{
    private const string Markdown = "# Fix flaky tests\n\nMake them deterministic.\n";
    private const string Summary = "Fix flaky UI tests in SettingsPanel";
    private const string Slug = "fix-flaky-tests";

    // ── Completion modal ───────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Click_CompletionModal_InvokedWithCorrectLabels()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");

        var fake = new FixTaskFakeRunner { Markdown = Markdown, Summary = Summary, Slug = Slug };

        var confirmCalls = new List<(string Title, string Message, string ConfirmLabel)>();

        var vm = NewViewModel(repo);
        vm.FixTaskRunnerFactory = _ => fake;
        vm.ShowConfirmationAsync = (title, message, confirmLabel) =>
        {
            confirmCalls.Add((title, message, confirmLabel));
            return Task.FromResult(true);
        };
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        await vm.CreateFixTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(confirmCalls, c =>
            c.Title == "Create task to fix" && c.ConfirmLabel == "Create");
        Assert.Contains(confirmCalls, c =>
            c.Title == "Fix task created" && c.ConfirmLabel == "View task");

        var completionCall = confirmCalls.First(c => c.Title == "Fix task created");
        Assert.Contains(Slug, completionCall.Message, StringComparison.Ordinal);
        Assert.Contains(Summary, completionCall.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Click_ViewTask_SelectsNewTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");

        var fake = new FixTaskFakeRunner { Markdown = Markdown, Summary = Summary, Slug = Slug };

        var vm = NewViewModel(repo);
        vm.FixTaskRunnerFactory = _ => fake;
        vm.ShowConfirmationAsync = (_, _, _) => Task.FromResult(true);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        await vm.CreateFixTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.SelectedTask);
        Assert.Equal(Slug, vm.SelectedTask.Id);
    }

    [AvaloniaFact]
    public async Task Click_DismissCompletionModal_KeepsOldSelection()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");

        var fake = new FixTaskFakeRunner { Markdown = Markdown, Summary = Summary, Slug = Slug };

        var vm = NewViewModel(repo);
        vm.FixTaskRunnerFactory = _ => fake;
        // First confirm returns true, second returns false (dismiss).
        var callCount = 0;
        vm.ShowConfirmationAsync = (_, _, _) =>
        {
            callCount++;
            return Task.FromResult(callCount == 1);
        };
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        await vm.CreateFixTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(vm.Tasks, t => t.Id == Slug);
        Assert.NotNull(vm.SelectedTask);
        Assert.Equal("flagged", vm.SelectedTask.Id);
    }

    // ── Error path ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Click_SubagentError_WritesNothingAndSurfacesMessage()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");

        var fake = new FixTaskFakeRunner { ThrowOnRun = true };

        var vm = NewViewModel(repo);
        vm.FixTaskRunnerFactory = _ => fake;
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        await vm.CreateFixTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // No new task files — only the pre-existing "flagged" task.
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var entries = Directory.Exists(tasksDir)
            ? Directory.GetFileSystemEntries(tasksDir)
            : [];
        Assert.Single(entries);
        Assert.Contains("flagged", Path.GetFileName(entries[0]), StringComparison.Ordinal);

        // Error surfaced and button re-enabled.
        Assert.Contains("Couldn't create fix task", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsCreatingFixTask);
        Assert.Equal("Create task to fix", vm.CreateFixTaskButtonLabel);
    }

    // ── Headless: ConfirmAsync auto-resolves ───────────────────────────────

    [AvaloniaFact]
    public async Task Click_Headless_AutoResolvesConfirmAsync()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");

        var fake = new FixTaskFakeRunner { Markdown = Markdown, Summary = Summary, Slug = Slug };

        var vm = NewViewModel(repo); // ShowConfirmationAsync = null
        vm.FixTaskRunnerFactory = _ => fake;
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        await vm.CreateFixTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(File.Exists(NestedMarkdownPath(repo.Root, Slug)));
        Assert.False(vm.IsCreatingFixTask);
    }

    // ── Busy state during creation ─────────────────────────────────────────

    [AvaloniaFact]
    public async Task Click_SetsIsCreatingFixTask_DuringExecution()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");

        var gate = new TaskCompletionSource();
        var fake = new GatedFixTaskRunner(Markdown, Summary, Slug, gate.Task);

        var vm = NewViewModel(repo);
        vm.FixTaskRunnerFactory = _ => fake;
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        // Start creation; runner parks on the gate.
        vm.CreateFixTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsCreatingFixTask);
        Assert.Equal("Creating fix task…", vm.CreateFixTaskButtonLabel);
        Assert.False(vm.CanCreateFixTaskPublic);

        // Release and pump.
        gate.SetResult();
        Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.False(vm.IsCreatingFixTask);
        Assert.Equal("Create task to fix", vm.CreateFixTaskButtonLabel);
    }
}
