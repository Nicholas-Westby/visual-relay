using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the "Create task to fix" button — CanExecute gating and the
/// happy path (author markdown + write new task + slug disambiguation).
/// </summary>
[Collection("Headless")]
public sealed partial class MainWindowViewModelFixTaskTests
{
    private const string AuthoredMarkdown = "# Fix flaky tests\n\nMake them deterministic.\n";
    private const string AuthoredSummary = "Fix flaky UI tests in SettingsPanel";
    private const string AuthoredSlug = "fix-flaky-tests";

    // ── Helpers ────────────────────────────────────────────────────────────

    private static MainWindowViewModel NewViewModel(TestRepository repo) =>
        new(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.Combine(repo.Root, ".xdg") })
        {
            RootPath = repo.Root,
            ShowConfirmationAsync = null,
        };

    private static TaskRowViewModel Row(MainWindowViewModel vm, string id) =>
        vm.Tasks.First(t => t.Id == id);

    /// <summary>
    /// Writes the minimum on-disk state that makes <c>HasSelectedTaskError</c>
    /// true for <paramref name="taskId"/>: a NEEDS-REVIEW flag file and a
    /// status.json with a Flagged stage entry.
    /// </summary>
    private static async Task WriteFlaggedRelayDataAsync(
        string root, string taskId, string reason = "swival timed out", int stage = 6)
    {
        var taskDirectory = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(taskDirectory, "NEEDS-REVIEW"),
            $"{reason}\nstage {stage}\n");

        var entries = new[]
        {
            new StageStatusEntry(stage, $"Stage {stage}", "Flagged", Error: reason),
        };
        await StageStatusRecord.WriteAsync(taskDirectory, entries);
    }

    private static string NestedMarkdownPath(string root, string slug) =>
        Path.Combine(root, "llm-tasks", slug, $"{slug}.md");

    // ── CanExecute gating ──────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task CanCreateFixTask_False_WhenNoFailedRun()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("clean", "# Clean\n");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "clean");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.False(vm.HasSelectedTaskError);
        Assert.False(vm.CanCreateFixTaskPublic);
    }

    [AvaloniaFact]
    public async Task CanCreateFixTask_True_WhenSelectedTaskHasFailedRun()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.True(vm.HasSelectedTaskError);
        Assert.True(vm.CanCreateFixTaskPublic);
    }

    [AvaloniaFact]
    public async Task CanCreateFixTask_False_WhenIsBusy()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        vm.IsBusy = true;
        Assert.False(vm.CanCreateFixTaskPublic);
        vm.IsBusy = false;
        Assert.True(vm.CanCreateFixTaskPublic);
    }

    [AvaloniaFact]
    public async Task CanCreateFixTask_False_WhenAlreadyCreatingFixTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.True(vm.CanCreateFixTaskPublic);

        vm.IsCreatingFixTask = true;
        Assert.False(vm.CanCreateFixTaskPublic);
        vm.IsCreatingFixTask = false;
        Assert.True(vm.CanCreateFixTaskPublic);
    }

    // ── Happy path: authors markdown and writes a new task ──────────────────

    [AvaloniaFact]
    public async Task Click_AuthorsAndWritesNewTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");

        var fake = new FixTaskFakeRunner
        {
            Markdown = AuthoredMarkdown,
            Summary = AuthoredSummary,
            Slug = AuthoredSlug,
        };

        var vm = NewViewModel(repo);
        vm.FixTaskRunnerFactory = _ => fake;
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.True(vm.CanCreateFixTaskPublic);

        await vm.CreateFixTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(fake.WasCalled);

        var expectedPath = NestedMarkdownPath(repo.Root, AuthoredSlug);
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(AuthoredMarkdown, await File.ReadAllTextAsync(expectedPath));
        Assert.Contains(vm.Tasks, t => t.Id == AuthoredSlug);
    }

    // ── Slug collision disambiguation ──────────────────────────────────────

    [AvaloniaFact]
    public async Task Click_SlugCollision_Disambiguates()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged");
        repo.WriteNestedTask(AuthoredSlug, "# Existing\n");

        var fake = new FixTaskFakeRunner
        {
            Markdown = AuthoredMarkdown,
            Summary = AuthoredSummary,
            Slug = AuthoredSlug,
        };

        var vm = NewViewModel(repo);
        vm.FixTaskRunnerFactory = _ => fake;
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        await vm.CreateFixTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // Pre-existing task must be untouched.
        Assert.Equal("# Existing\n",
            await File.ReadAllTextAsync(NestedMarkdownPath(repo.Root, AuthoredSlug)));

        // Disambiguated slug must be written and appear in the queue.
        Assert.True(File.Exists(NestedMarkdownPath(repo.Root, $"{AuthoredSlug}-2")));
        Assert.Contains(vm.Tasks, t => t.Id == $"{AuthoredSlug}-2");
    }
}
