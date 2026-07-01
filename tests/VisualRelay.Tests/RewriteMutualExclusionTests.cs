using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Spec-mandated view-model tests for the "Rewrite with AI" feature's mutual
/// exclusion with edits, runs, and drains, plus the completion handler keying
/// off the CAPTURED rewritten-task id (not the live selection).
/// </summary>
[Collection("Headless")]
public sealed class RewriteMutualExclusionTests
{
    private const string RewrittenSpec = "# Rewritten\n\nBetter spec.\n";

    // Point the VR-owned nono profile at a throwaway XDG dir under the repo so the
    // rewrite path's NonoProfileEnsurer.EnsureAsync writes there — never the real
    // developer/CI ~/.config (and never throws when $HOME is unset on CI).
    private static MainWindowViewModel NewViewModel(TestRepository repo) =>
        new(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.Combine(repo.Root, ".xdg") })
        {
            RootPath = repo.Root,
            ShowConfirmationAsync = null,
        };

    private static TaskRowViewModel Row(MainWindowViewModel vm, string id) =>
        vm.Tasks.First(t => t.Id == id);

    // ── Confirm-button label per call site ─────────────────────────────────

    [AvaloniaFact]
    public async Task RewriteConfirmation_UsesRewriteAndReplaceConfirmLabel()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("subject", "# Subject\n");

        string? capturedConfirmLabel = null;
        var vm = NewViewModel(repo);
        // Capture the confirm label, then cancel so no runner is needed.
        vm.ShowConfirmationAsync = (_, _, confirmLabel) =>
        {
            capturedConfirmLabel = confirmLabel;
            return Task.FromResult(false);
        };
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "subject");

        await vm.RewriteSelectedTaskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Rewrite and Replace", capturedConfirmLabel);
    }

    // ── FIX 5: CanRewriteSelected rules ─────────────────────────────────────

    [AvaloniaFact]
    public async Task CanRewriteSelected_TrueForFreshPendingTask_AndIndependentOfIsBusy()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("fresh", "# Fresh\n");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "fresh");

        Assert.True(vm.CanRewriteSelectedPublic,
            "a CompletedStageCount==0, non-archived, non-running, non-rewriting task is rewritable");

        // Rewrites run concurrently with a drain — IsBusy must NOT gate them.
        vm.IsBusy = true;
        Assert.True(vm.CanRewriteSelectedPublic,
            "CanRewriteSelected must be independent of IsBusy");
        vm.IsBusy = false;
    }

    [AvaloniaFact]
    public async Task CanRewriteSelected_FalseWhenAlreadyRun_OrArchived()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("fresh", "# Fresh\n");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();

        // Archived selection → not rewritable.
        var archivedRow = new TaskRowViewModel(
            Row(vm, "fresh").Task with { IsArchived = true });
        vm.SelectedTask = archivedRow;
        Assert.False(vm.CanRewriteSelectedPublic, "archived tasks are not rewritable");

        // CompletedStageCount != 0 (already run) → not rewritable.
        var ranRow = new TaskRowViewModel(
            Row(vm, "fresh").Task with { CompletedStageCount = 2 });
        vm.SelectedTask = ranRow;
        Assert.False(vm.CanRewriteSelectedPublic,
            "a task that has already run (CompletedStageCount != 0) is not rewritable");
    }

    [AvaloniaFact]
    public async Task CanRewriteSelected_FalseForFlatTask_WhereTaskDirectoryIsTheSharedRoot()
    {
        // A flat (non-nested) task's TaskDirectory is the SHARED llm-tasks/ root.
        // The rewrite copy-back and the revert both delete TaskDirectory
        // recursively, so rewriting a still-flat task would wipe every sibling.
        // Selection normally promotes a task to nested, so this guard is the
        // defence for when that promotion did NOT take effect (e.g. it threw).
        // Setting SelectedTask runs the promotion synchronously, so to exercise
        // the guard we model the promotion-failed state: select the task, then
        // force the SELECTED row back to flat before evaluating CanRewrite.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("nested", "# Nested\n");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();

        vm.SelectedTask = Row(vm, "nested");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        // Re-flatten the live selection: TaskDirectory == the shared llm-tasks/ root.
        vm.SelectedTask!.Task = vm.SelectedTask.Task with
        {
            IsNested = false,
            TaskDirectory = Path.Combine(repo.Root, "llm-tasks"),
        };

        Assert.False(vm.CanRewriteSelectedPublic,
            "a non-nested task (TaskDirectory == shared root) must not be rewritable");
    }

    // ── FIX 5: edit is blocked (right reason) while rewriting ───────────────

    [AvaloniaFact]
    public async Task EditAndRewrite_AreMutuallyExclusive_WhileRewriting()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("subject", "# Subject\n");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "subject");

        var gate = new TaskCompletionSource();
        vm.RewriteRunnerFactory = _ => new GatedRewriteRunner(RewrittenSpec, gate.Task);

        // Start the rewrite; it parks inside the runner until we release the gate.
        vm.RewriteSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsSelectedTaskRewriting, "the subject task must be marked rewriting");
        Assert.False(vm.EditSelectedTaskCommand.CanExecute(null),
            "editing a task that is being rewritten must be blocked");
        Assert.Equal("Cannot edit a task while it's being rewritten.", vm.EditBlockedReason);
        Assert.False(vm.CanRewriteSelectedPublic,
            "a task already being rewritten cannot start a second rewrite");

        // Release the rewrite and drain the completion continuation.
        gate.SetResult();
        await vm.WaitForRewriteToFinishForTests("subject");
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsSelectedTaskRewriting);
        Assert.True(vm.EditSelectedTaskCommand.CanExecute(null),
            "editing is allowed again once the rewrite finishes");
    }

    // ── FIX 5: a drain skips a rewriting task ───────────────────────────────

    [AvaloniaFact]
    public async Task Drain_SkipsATaskThatIsBeingRewritten()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("rewriting", "# Rewriting\n");
        repo.WriteNestedTask("normal", "# Normal\n");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "rewriting");

        var gate = new TaskCompletionSource();
        vm.RewriteRunnerFactory = _ => new GatedRewriteRunner(RewrittenSpec, gate.Task);
        vm.RewriteSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsSelectedTaskRewriting);

        // The set of task ids the drain would execute excludes the rewriting one.
        var drainable = vm.DrainableTaskIdsForTests();
        Assert.DoesNotContain("rewriting", drainable);
        Assert.Contains("normal", drainable);

        gate.SetResult();
        await vm.WaitForRewriteToFinishForTests("rewriting");
        Dispatcher.UIThread.RunJobs();
    }

    // ── FIX 4: completion reloads the CAPTURED rewritten task, not the live
    //          selection that may have changed mid-rewrite ──────────────────

    [AvaloniaFact]
    public async Task Completion_ReloadsAndReportsCapturedTaskId_NotChangedSelection()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("alpha", "# Alpha\n");
        repo.WriteNestedTask("beta", "# Beta\n");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "alpha");

        var gate = new TaskCompletionSource();
        vm.RewriteRunnerFactory = _ => new GatedRewriteRunner(RewrittenSpec, gate.Task);

        // Rewrite ALPHA.
        vm.RewriteSelectedTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsSelectedTaskRewriting);

        // The user switches selection to BETA while ALPHA's rewrite is in flight.
        vm.SelectedTask = Row(vm, "beta");
        Dispatcher.UIThread.RunJobs();

        // Let ALPHA's rewrite finish.
        gate.SetResult();
        await vm.WaitForRewriteToFinishForTests("alpha");
        Dispatcher.UIThread.RunJobs();

        // The completion handler must reload/select and report against ALPHA
        // (the captured id), not BETA (the live selection).
        Assert.NotNull(vm.SelectedTask);
        Assert.Equal("alpha", vm.SelectedTask.Id);
        Assert.Contains("alpha", vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", vm.StatusText, StringComparison.Ordinal);
    }
}

/// <summary>
/// A fake <see cref="ISubagentRunner"/> that parks inside <see cref="RunAsync"/>
/// until an external gate completes, then writes the rewritten spec — letting a
/// view-model test observe the in-flight rewriting state deterministically.
/// </summary>
internal sealed class GatedRewriteRunner(string newContent, Task gate) : ISubagentRunner
{
    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        foreach (var file in invocation.Manifest)
        {
            var fullPath = Path.Combine(invocation.TargetRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, newContent, ct);
        }

        return new SubagentResult(
            RawText: "```json\n{\"summary\":\"rewritten\"}\n```",
            Json: "{\"summary\":\"rewritten\"}",
            IsValid: true,
            Error: null);
    }
}
