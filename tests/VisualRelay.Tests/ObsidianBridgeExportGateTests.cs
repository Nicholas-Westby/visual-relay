using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.ObsidianBridge;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// VM integration tests for the export-ledger gate on the Obsidian bridge
/// reconcile pass. Exercises <c>RunObsidianBridgeScanAsync</c> directly.
/// </summary>
[Collection("Headless")]
public sealed class ObsidianBridgeExportGateTests : IDisposable
{
    private readonly string _tempHome;

    public ObsidianBridgeExportGateTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "vr-obsidian-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
    }

    public void Dispose()
    {
        TestFileSystem.DeleteDirectoryResilient(_tempHome);
    }

    private static (string VaultRoot, string RepoRoot) SetupDirs()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "vr-obs-gate-tests",
            "vault-" + Guid.NewGuid().ToString("N")[..8]);
        var repoRoot = Path.Combine(Path.GetTempPath(), "vr-obs-gate-repo",
            "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(vaultRoot);
        Directory.CreateDirectory(repoRoot);
        return (vaultRoot, repoRoot);
    }

    private static void Cleanup(string vaultRoot, string repoRoot)
    {
        TestFileSystem.DeleteDirectoryResilient(vaultRoot);
        TestFileSystem.DeleteDirectoryResilient(repoRoot);
    }

    private MainWindowViewModel CreateViewModel(
        string repoRoot, string vaultRoot, DictionaryEnvironmentAccessor env,
        bool bridgeEnabled = true)
    {
        var configDir = Path.Combine(repoRoot, ".relay");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"),
            """{"testCmd": "true", "logSources": []}""");

        env["HOME"] = _tempHome;
        KeyEnvFile.Upsert("VR_OBSIDIAN_ENABLED",
            bridgeEnabled ? "true" : "false", env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_VAULT_ROOT",
            vaultRoot.Replace("\\", "/"), env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_POLL_SECONDS", "60", env);

        return new MainWindowViewModel(environmentAccessor: env) { RootPath = repoRoot };
    }

    private static void WriteStageReport(string repoRoot, string taskId,
        int stage, string timestamp)
    {
        var dir = Path.Combine(repoRoot, ".relay", taskId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"stage{stage}-attempt1.report.json"),
            $$"""{"timestamp":"{{timestamp}}","model":"cheap","result":{"outcome":"success"},"stats":{"total_llm_time_s":1.0},"timeline":[{"type":"llm_call","prompt_tokens_est":100}]}""");
    }

    private static void WriteCompletedTask(string repoRoot, string taskId,
        string markdown = "# Task\n\nContent.")
    {
        var tasksDir = Path.Combine(repoRoot, "llm-tasks");
        Directory.CreateDirectory(tasksDir);
        File.WriteAllText(Path.Combine(tasksDir, $"DONE-{taskId}.md"), markdown);
    }

    private static string LedgerPath(string vaultRoot, string repoName)
        => Path.Combine(vaultRoot, repoName, ".vr-export-ledger.json");

    private static string? FindNote(string vaultRoot, string repoName, string taskId)
    {
        var completedRoot = Path.Combine(vaultRoot, repoName, "Completed");
        if (!Directory.Exists(completedRoot)) return null;
        return Directory
            .EnumerateFiles(completedRoot, $"{taskId}.md", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    private static bool AnyCompletedNote(string vaultRoot, string repoName)
    {
        var completedRoot = Path.Combine(vaultRoot, repoName, "Completed");
        if (!Directory.Exists(completedRoot)) return false;
        return Directory.EnumerateFiles(completedRoot, "*.md", SearchOption.AllDirectories)
            .Any(f => !ObsidianVaultLayout.ReservedFileNames.Contains(Path.GetFileName(f)));
    }

    [AvaloniaFact]
    public async Task NoRelayArtifacts_ScanWritesNoNote()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            WriteCompletedTask(repoRoot, "never-ran", "# Never Ran\n\nArchived by hand.");
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            Assert.False(AnyCompletedNote(vaultRoot, Path.GetFileName(repoRoot)));
        }
        finally { Cleanup(vaultRoot, repoRoot); }
    }

    [AvaloniaFact]
    public async Task NoRelayArtifacts_TwoScans_WritesNoNotes()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            WriteCompletedTask(repoRoot, "never-ran-2", "# Never Ran 2");
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            Assert.False(AnyCompletedNote(vaultRoot, Path.GetFileName(repoRoot)));
        }
        finally { Cleanup(vaultRoot, repoRoot); }
    }

    [AvaloniaFact]
    public async Task MetricHavingTask_NoteDeletedAfterExport_NotRecreated()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            WriteCompletedTask(repoRoot, "has-metrics", "# Has Metrics\n\nRan successfully.");
            WriteStageReport(repoRoot, "has-metrics", 1, "2026-06-20T14:00:00+00:00");
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            var repoName = Path.GetFileName(repoRoot);
            var notePath = FindNote(vaultRoot, repoName, "has-metrics");
            Assert.NotNull(notePath);
            File.Delete(notePath);
            Assert.False(File.Exists(notePath));
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            Assert.Null(FindNote(vaultRoot, repoName, "has-metrics"));
        }
        finally { Cleanup(vaultRoot, repoRoot); }
    }

    [AvaloniaFact]
    public async Task FreshVault_NoLedger_NoNotes_BackfillsAllMetricHavingTasks()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            WriteCompletedTask(repoRoot, "alpha", "# Alpha\n\nFirst.");
            WriteStageReport(repoRoot, "alpha", 1, "2026-06-15T10:00:00+00:00");
            WriteCompletedTask(repoRoot, "beta", "# Beta\n\nSecond.");
            WriteStageReport(repoRoot, "beta", 1, "2026-06-16T12:00:00+00:00");
            WriteCompletedTask(repoRoot, "no-metrics", "# No Metrics\n\nHand-archived.");
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            var repoName = Path.GetFileName(repoRoot);
            Assert.NotNull(FindNote(vaultRoot, repoName, "alpha"));
            Assert.NotNull(FindNote(vaultRoot, repoName, "beta"));
            Assert.Null(FindNote(vaultRoot, repoName, "no-metrics"));
            Assert.True(File.Exists(LedgerPath(vaultRoot, repoName)));
        }
        finally { Cleanup(vaultRoot, repoRoot); }
    }

    [AvaloniaFact]
    public async Task PreLedgerVault_ExistingNotes_SeedsLedger_WritesNoNewNotes()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            WriteCompletedTask(repoRoot, "existing-task", "# Existing\n\nAlready exported.");
            WriteStageReport(repoRoot, "existing-task", 1, "2026-06-20T08:00:00+00:00");
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            var repoName = Path.GetFileName(repoRoot);
            Assert.NotNull(FindNote(vaultRoot, repoName, "existing-task"));
            var ledgerPath = LedgerPath(vaultRoot, repoName);
            if (File.Exists(ledgerPath)) File.Delete(ledgerPath);
            Assert.False(File.Exists(ledgerPath));
            WriteCompletedTask(repoRoot, "second-task", "# Second\n\nAlso completed.");
            WriteStageReport(repoRoot, "second-task", 1, "2026-06-21T09:00:00+00:00");
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            Assert.True(File.Exists(ledgerPath));
            Assert.Null(FindNote(vaultRoot, repoName, "second-task"));
        }
        finally { Cleanup(vaultRoot, repoRoot); }
    }

    [AvaloniaFact]
    public async Task ExportSummaryOnCompletion_RecordsTaskIdInLedger()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            var taskId = "drain-me";
            var nestedDir = Path.Combine(repoRoot, "llm-tasks", taskId);
            Directory.CreateDirectory(nestedDir);
            File.WriteAllText(Path.Combine(nestedDir, $"{taskId}.md"), "# Drain Me\n\nRun through drain.");
            File.WriteAllText(Path.Combine(repoRoot, "llm-tasks", $"DONE-{taskId}.md"), "# Drain Me");
            WriteStageReport(repoRoot, taskId, 1, "2026-06-22T10:00:00+00:00");
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            var repoName = Path.GetFileName(repoRoot);
            var callbacks = viewModel.CreateDrainLifecycleCallbacks();
            callbacks.OnExecuteStarted!(taskId);
            var outcome = new RelayTaskOutcome(
                taskId, RelayTaskOutcomeStatus.Committed, "hash", "abc1234", null);
            callbacks.OnExecuteCompleted!(taskId, outcome);
            await TestWaits.ForFileAsync(
                Path.Combine(vaultRoot, repoName, "Completed"),
                predicate: () => FindNote(vaultRoot, repoName, taskId) is not null);
            Assert.True(File.Exists(LedgerPath(vaultRoot, repoName)));
        }
        finally { Cleanup(vaultRoot, repoRoot); }
    }

    [AvaloniaFact]
    public async Task CorruptLedger_TreatedAsAbsent_NoCrash()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            WriteCompletedTask(repoRoot, "task-a", "# Task A\n\nFirst.");
            WriteStageReport(repoRoot, "task-a", 1, "2026-06-23T10:00:00+00:00");
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            var repoName = Path.GetFileName(repoRoot);
            var ledgerPath = LedgerPath(vaultRoot, repoName);
            await File.WriteAllTextAsync(ledgerPath, "this is not valid json {{{ corrupted");
            var noteA = FindNote(vaultRoot, repoName, "task-a");
            if (noteA is not null) File.Delete(noteA);
            WriteCompletedTask(repoRoot, "task-b", "# Task B\n\nSecond.");
            WriteStageReport(repoRoot, "task-b", 1, "2026-06-24T11:00:00+00:00");
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);
            Assert.Null(FindNote(vaultRoot, repoName, "task-a"));
            Assert.Null(FindNote(vaultRoot, repoName, "task-b"));
        }
        finally { Cleanup(vaultRoot, repoRoot); }
    }
}
