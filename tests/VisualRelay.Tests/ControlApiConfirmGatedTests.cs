using System.Text.Json;
using Avalonia.Threading;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for confirm-gated commands driven through the localhost control API
/// (<see cref="ControlApi.InvokeCommandAsync"/>). A confirm-gated command (e.g.
/// mark-done) must run to completion and take effect WITHOUT opening the
/// interactive modal — and a destructive command must be refused unless the
/// caller passes an explicit <c>{"confirm":true}</c>. The human-GUI seam (a real
/// button click) must still route to the confirmation delegate and honor Cancel.
/// </summary>
[Collection("Headless")]
public sealed class ControlApiConfirmGatedTests
{
    private static async Task<(ControlApi Api, MainWindowViewModel Vm)> NewLoadedAsync(
        TestRepository repo, string taskId)
    {
        // Route ui-state persistence (and the rewrite path's XDG writes) to a
        // throwaway dir under the repo so tests never touch the real ~/.config.
        var vm = new MainWindowViewModel(
            new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.Combine(repo.Root, ".xdg") })
        {
            RootPath = repo.Root,
        };
        await vm.LoadInitialAsync();
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);

        await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedTask = vm.Tasks.Single(t => t.Id == taskId));
        Dispatcher.UIThread.RunJobs();
        await (vm.LastSelectionLoad ?? Task.CompletedTask);
        return (api, vm);
    }

    [AvaloniaFact]
    public async Task MarkDone_ViaApi_WithConfirm_CompletesAndArchives_WithoutDialog()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], archiveOnDone: true);
        repo.WriteNestedTask("windows-support", "# Windows Support\n");

        var (api, vm) = await NewLoadedAsync(repo, "windows-support");

        // A modal would invoke this delegate; the API path must NOT.
        var dialogShown = false;
        vm.ShowConfirmationAsync = (_, _, _) => { dialogShown = true; return Task.FromResult(true); };

        var (status, json) = await api.InvokeCommandAsync("mark-done", "{\"confirm\":true}");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200, status);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        Assert.False(dialogShown, "API-driven mark-done must not open the interactive confirmation dialog");

        // Effect: the task left the queue, observable in /state.
        var stateJson = await api.BuildStateJsonAsync();
        using var stateDoc = JsonDocument.Parse(stateJson);
        var ids = stateDoc.RootElement.GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.DoesNotContain("windows-support", ids);
    }

    [AvaloniaFact]
    public async Task MarkDone_ViaApi_WithoutConfirm_IsRefused_AndNoOp()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], archiveOnDone: true);
        repo.WriteNestedTask("keep-me", "# Keep Me\n");

        var (api, vm) = await NewLoadedAsync(repo, "keep-me");
        var dialogShown = false;
        vm.ShowConfirmationAsync = (_, _, _) => { dialogShown = true; return Task.FromResult(true); };

        var (status, json) = await api.InvokeCommandAsync("mark-done", null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(409, status);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("confirmation required", doc.RootElement.GetProperty("error").GetString());
        }

        Assert.False(dialogShown, "a refused destructive command must not open a dialog");

        // No effect: the task is still queued.
        Assert.Contains(vm.Tasks, t => t.Id == "keep-me");
    }

    [AvaloniaFact]
    public async Task MarkDone_HumanGuiSeam_StillRoutesToConfirmationDelegate_AndHonorsCancel()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], archiveOnDone: true);
        repo.WriteNestedTask("ask-me", "# Ask Me\n");

        var (_, vm) = await NewLoadedAsync(repo, "ask-me");

        // Human path: a real (non-API) invocation must consult the modal delegate
        // and honor Cancel (return false → no archive).
        var dialogShown = false;
        vm.ShowConfirmationAsync = (_, _, _) => { dialogShown = true; return Task.FromResult(false); };

        await vm.MarkSelectedTaskDoneCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(dialogShown, "a real button click must still open the confirmation modal");
        Assert.Contains(vm.Tasks, t => t.Id == "ask-me"); // Cancel aborted the archive
    }

    [AvaloniaFact]
    public async Task RewriteSelected_ViaApi_WithConfirm_AutoConfirms_WithoutDialog()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("subject", "# Subject\n");

        var (api, vm) = await NewLoadedAsync(repo, "subject");

        var dialogShown = false;
        vm.ShowConfirmationAsync = (_, _, _) => { dialogShown = true; return Task.FromResult(true); };

        // Park the rewrite inside the runner so the in-flight state is observable
        // deterministically without running a real subagent. (Proves the SAME
        // confirmation seam fix covers rewrite, not just mark-done.)
        var gate = new TaskCompletionSource();
        vm.RewriteRunnerFactory = _ => new GatedRewriteRunner("# Rewritten\n", gate.Task);

        var (status, json) = await api.InvokeCommandAsync("rewrite-selected", "{\"confirm\":true}");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200, status);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        Assert.False(dialogShown, "API-driven rewrite must not open the confirmation dialog");
        Assert.True(vm.IsSelectedTaskRewriting,
            "the rewrite must have actually started — confirmation was auto-resolved, not hung");

        // Release the gate and let the rewrite settle so no temp snapshot leaks.
        gate.SetResult();
        await vm.WaitForRewriteToFinishForTests("subject");
        Dispatcher.UIThread.RunJobs();
    }
}
