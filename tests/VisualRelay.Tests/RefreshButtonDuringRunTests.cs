using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.Tests;

/// <summary>
/// Tests that the Refresh button (and its control-API equivalent) works
/// during an active queue drain — currently double-gated on
/// <c>IsBusy</c> in both <c>CanRefresh</c> and <c>RunBusyAsync</c>.
/// After the fix: CanRefresh drops the busy gate, RefreshAsync
/// reloads directly when busy, StatusText stays honest, and the
/// running row survives the reload.
/// </summary>
[Collection("Headless")]
public sealed partial class RefreshButtonDuringRunTests
{
    // ── Busy-tolerant CanExecute ──────────────────────────────────────

    [AvaloniaFact]
    public async Task RefreshCommand_CanExecute_True_WhenBusy()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await vm.LoadInitialAsync();

        vm.IsBusy = true;

        Assert.True(vm.RefreshCommand.CanExecute(null),
            "Refresh must be executable while IsBusy is true — " +
            "the busy gate must be removed from CanRefresh.");
    }

    // ── Busy-tolerant task reload ─────────────────────────────────────

    [AvaloniaFact]
    public async Task RefreshCommand_WhenBusy_ReloadsTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        Assert.Single(vm.Tasks, t => t.Id == "alpha");

        // Simulate a new task folder appearing while a drain runs.
        vm.IsBusy = true;
        repo.WriteTask("beta", "# Beta\n");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains(vm.Tasks, t => t.Id == "beta");
    }

    // ── Running task row survives mid-drain reload ────────────────────

    [AvaloniaFact]
    public async Task RefreshCommand_WhenBusy_PreservesRunningTaskRowAndSelection()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("running", "# Running\n");
        repo.WriteTask("beta", "# Beta\n");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await vm.LoadInitialAsync();

        vm.RestoreRunningTaskState("running", 5, "Author-tests");
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "running");

        vm.IsBusy = true;
        // Prove the reload actually happened by adding a task after initial load.
        repo.WriteTask("gamma", "# Gamma\n");

        await vm.RefreshCommand.ExecuteAsync(null);

        // Running row preserved with stage info.
        var runningRow = vm.Tasks.Single(t => t.Id == "running");
        Assert.True(runningRow.IsRunning);
        Assert.Equal("Stage 05 · Author-tests", runningRow.MetricsLine);

        // Selection preserved on the same task.
        Assert.NotNull(vm.SelectedTask);
        Assert.Equal("running", vm.SelectedTask!.Id);

        // New task appeared — confirms the reload ran.
        Assert.Contains(vm.Tasks, t => t.Id == "gamma");
    }

    // ── StatusText stays honest after mid-drain reload ────────────────

    [AvaloniaFact]
    public async Task ToggleArchive_WhenBusy_PreservesRunningStatusText()
    {
        // The toggle path currently overwrites StatusText with
        // FormatQueueStatus() even during a run — a known bug.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await vm.LoadInitialAsync();

        vm.IsBusy = true;
        vm.StatusText = "Running alpha · Stage 05 · Author-tests";

        await vm.ToggleArchiveCommand.ExecuteAsync(null);

        Assert.DoesNotContain("pending", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task RefreshCommand_WhenBusy_PreservesRunningStatusText()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await vm.LoadInitialAsync();

        vm.IsBusy = true;
        vm.StatusText = "Running alpha · Stage 05 · Author-tests";
        // Prove reload ran.
        repo.WriteTask("beta", "# Beta\n");

        await vm.RefreshCommand.ExecuteAsync(null);

        // New task appeared — confirms the reload ran.
        Assert.Contains(vm.Tasks, t => t.Id == "beta");

        // StatusText must not be overwritten with idle queue status.
        Assert.DoesNotContain("pending", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    // ── Idle refresh regression pin ───────────────────────────────────

    [AvaloniaFact]
    public async Task RefreshCommand_WhenIdle_BehavesAsBefore()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await vm.LoadInitialAsync();

        // Idle refresh must still be executable.
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.IsBusy);

        repo.WriteTask("beta", "# Beta\n");
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains(vm.Tasks, t => t.Id == "beta");
        Assert.Contains("pending", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    // ── Control API "refresh" mapping ─────────────────────────────────
    // Currently the API checks CanExecute(null) (which returns false when
    // busy) and refuses with 409.  After the fix the refresh command is
    // executable while busy and the API must return 200.

    [AvaloniaFact]
    public async Task ControlApi_Refresh_WhenBusy_Returns200()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await vm.LoadInitialAsync();

        vm.IsBusy = true;
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);

        var (status, _) = await api.InvokeCommandAsync("refresh", null);

        Assert.Equal(200, status);
    }
}
