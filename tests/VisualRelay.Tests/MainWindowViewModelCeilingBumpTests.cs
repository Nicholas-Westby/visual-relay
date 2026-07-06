using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the "+10 minutes" ceiling bump button — IsCeilingTimeoutError
/// derivation, CanExecute gating, and the config-write happy path.
/// </summary>
[Collection("Headless")]
public sealed partial class MainWindowViewModelCeilingBumpTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static MainWindowViewModel NewViewModel(TestRepository repo) =>
        new(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.Combine(repo.Root, ".xdg") })
        {
            RootPath = repo.Root,
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

    // ── IsCeilingTimeoutError ──────────────────────────────────────────────

    [AvaloniaFact]
    public async Task IsCeilingTimeoutError_True_WhenErrorContainsAbsoluteCeiling()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged",
            "swival timed out after 30m 00s (1800000 ms) absolute ceiling. Last signal: cpu, silence: 970ms.");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.True(vm.HasSelectedTaskError);
        Assert.True(vm.IsCeilingTimeoutError);
    }

    [AvaloniaFact]
    public async Task IsCeilingTimeoutError_False_WhenErrorDoesNotContainAbsoluteCeiling()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged", "some other error");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.True(vm.HasSelectedTaskError);
        Assert.False(vm.IsCeilingTimeoutError);
    }

    [AvaloniaFact]
    public async Task IsCeilingTimeoutError_False_WhenNoError()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("clean", "# Clean\n");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "clean");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.False(vm.HasSelectedTaskError);
        Assert.False(vm.IsCeilingTimeoutError);
    }

    // ── CanExecute gating ──────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task CanBumpCeiling_False_WhenIsBusy()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged",
            "swival timed out after 30m 00s (1800000 ms) absolute ceiling.");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.True(vm.IsCeilingTimeoutError);
        Assert.True(vm.CanBumpCeilingPublic);

        vm.IsBusy = true;
        Assert.False(vm.CanBumpCeilingPublic);
        vm.IsBusy = false;
        Assert.True(vm.CanBumpCeilingPublic);
    }

    [AvaloniaFact]
    public async Task CanBumpCeiling_False_WhenNotCeilingError()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged", "some other error");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.True(vm.HasSelectedTaskError);
        Assert.False(vm.IsCeilingTimeoutError);
        Assert.False(vm.CanBumpCeilingPublic);
    }

    [AvaloniaFact]
    public async Task CanBumpCeiling_False_WhenNoRootPath()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged",
            "swival timed out after 30m 00s (1800000 ms) absolute ceiling.");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.True(vm.IsCeilingTimeoutError);
        Assert.True(vm.CanBumpCeilingPublic);

        vm.RootPath = string.Empty;
        Assert.False(vm.CanBumpCeilingPublic);
    }

    // ── Happy path: bump persists config and updates status text ──────────

    [AvaloniaFact]
    public async Task Click_BumpPersistsAndUpdatesStatus()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("flagged", "# Flagged\n");
        // Seed the config with a known subagent timeout so the bump is predictable.
        RelayConfigWriter.UpsertSubagentTimeout(repo.Root, 1_800_000);
        await WriteFlaggedRelayDataAsync(repo.Root, "flagged",
            "swival timed out after 30m 00s (1800000 ms) absolute ceiling.");

        var vm = NewViewModel(repo);
        await vm.LoadInitialAsync();
        vm.SelectedTask = Row(vm, "flagged");
        await (vm.LastSelectionLoad ?? Task.CompletedTask);

        Assert.True(vm.IsCeilingTimeoutError);
        Assert.True(vm.CanBumpCeilingPublic);

        await vm.BumpCeilingCommand.ExecuteAsync(null);

        // Verify config was bumped by exactly 600_000 ms.
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(2_400_000, result.Config.SubagentTimeoutMilliseconds);

        // Status text should mention the new minute value and "next run".
        Assert.Contains("40m", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next run", vm.StatusText, StringComparison.OrdinalIgnoreCase);

        Assert.False(vm.IsBusy);
    }
}
