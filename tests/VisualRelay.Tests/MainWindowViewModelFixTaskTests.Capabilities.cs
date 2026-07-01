using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// Capability-scope tests: verify the fix-task author subagent's
/// synthetic stage does not grant write access or arbitrary command
/// execution. The <c>StageInvocation</c> captured by the fake runner
/// must reflect <c>Files: "none"</c> and <c>Commands: "none"</c>.
/// </summary>
public sealed partial class MainWindowViewModelFixTaskTests
{
    // ── Happy path: readonly capabilities ──────────────────────────────────

    [AvaloniaFact]
    public async Task Click_FixTaskAuthorInvocation_HasReadOnlyCapabilities()
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

        await vm.CreateFixTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // Invocation must have been captured.
        Assert.NotNull(fake.LastInvocation);

        // Capabilities must be read-only — no file writes, no command execution.
        Assert.Equal("none", fake.LastInvocation.Stage.Files);
        Assert.Equal("none", fake.LastInvocation.Stage.Commands);

        // The happy path still works: task was created.
        Assert.True(fake.WasCalled);
        var expectedPath = NestedMarkdownPath(repo.Root, AuthoredSlug);
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(AuthoredMarkdown, await File.ReadAllTextAsync(expectedPath));
    }

    // ── Error path: readonly capabilities ──────────────────────────────────

    [AvaloniaFact]
    public async Task Click_FixTaskAuthorInvocation_ErrorPath_HasReadOnlyCapabilities()
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

        // Invocation must have been captured even on the error path
        // (the runner throws *after* capturing the invocation).
        Assert.NotNull(fake.LastInvocation);

        // Capabilities must be read-only.
        Assert.Equal("none", fake.LastInvocation.Stage.Files);
        Assert.Equal("none", fake.LastInvocation.Stage.Commands);

        // Error path: no task file written.
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        var entries = Directory.Exists(tasksDir)
            ? Directory.GetFileSystemEntries(tasksDir)
            : [];
        Assert.Single(entries);
        Assert.Contains("flagged", Path.GetFileName(entries[0]), StringComparison.Ordinal);

        // Error surfaced.
        Assert.Contains("Couldn't create fix task", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }
}
