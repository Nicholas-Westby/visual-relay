using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// VM integration tests for the Obsidian bridge feature. These construct
/// a real MainWindowViewModel and exercise the testable cycle method
/// (<c>RunObsidianBridgeScanAsync</c>) directly — no timer spinning.
/// </summary>
[Collection("Headless")]
public sealed class ObsidianBridgeVmTests : IDisposable
{
    private readonly string _tempHome;

    public ObsidianBridgeVmTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "vr-obsidian-vm-home", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
    }

    public void Dispose()
    {
        TestFileSystem.DeleteDirectoryResilient(_tempHome);
    }

    private static (string VaultRoot, string RepoRoot) SetupDirs()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "vr-obsidian-vm-tests",
            Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(Path.GetTempPath(), "vr-obsidian-vm-repo",
            Guid.NewGuid().ToString("N"));
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
        // Write a minimal config so the repo is valid.
        var configDir = Path.Combine(repoRoot, ".relay");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "config.json"),
            """
            {
              "testCmd": "true",
              "logSources": []
            }
            """);

        // Set up the obsidian bridge settings via the env accessor.
        env["HOME"] = _tempHome;
        KeyEnvFile.Upsert("VR_OBSIDIAN_ENABLED",
            bridgeEnabled ? "true" : "false",
            env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_VAULT_ROOT",
            vaultRoot.Replace("\\", "/"),
            env);
        KeyEnvFile.Upsert("VR_OBSIDIAN_POLL_SECONDS",
            "60",
            env);

        var viewModel = new MainWindowViewModel(environmentAccessor: env)
        {
            RootPath = repoRoot
        };
        return viewModel;
    }

    // ── RunObsidianBridgeScanAsync imports tasks ──────────────────────

    [AvaloniaFact]
    public async Task RunObsidianBridgeScanAsync_ImportsStableFileAndCreatesTask()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();

            // Set up the vault layout and drop a task file.
            var newTasksDir = Path.Combine(vaultRoot, RootFolderDisplay.Name(repoRoot), "New Tasks");
            Directory.CreateDirectory(newTasksDir);

            var sourcePath = Path.Combine(newTasksDir, "obsidian-task.md");
            File.WriteAllText(sourcePath, "# Obsidian Task\n\nMake something happen.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            // Run the bridge scan cycle directly.
            var imported = await Dispatcher.UIThread.InvokeAsync(
                viewModel.RunObsidianBridgeScanAsync);

            // At least one task should have been imported.
            Assert.True(imported >= 1);

            // The task should now exist in llm-tasks/.
            var taskDir = Path.Combine(repoRoot, "llm-tasks");
            var subdirs = Directory.GetDirectories(taskDir);
            Assert.NotEmpty(subdirs);
        }
        finally
        {
            Cleanup(vaultRoot, repoRoot);
        }
    }

    // ── No-op when disabled ───────────────────────────────────────────

    [AvaloniaFact]
    public async Task RunObsidianBridgeScanAsync_NoOpsWhenDisabled()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env, bridgeEnabled: false);
            await viewModel.LoadInitialAsync();

            // Drop a task file in the vault — it should be ignored.
            var newTasksDir = Path.Combine(vaultRoot, RootFolderDisplay.Name(repoRoot), "New Tasks");
            Directory.CreateDirectory(newTasksDir);

            var sourcePath = Path.Combine(newTasksDir, "should-be-ignored.md");
            File.WriteAllText(sourcePath, "# Ignored\n\nShould not be imported.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var imported = await Dispatcher.UIThread.InvokeAsync(
                viewModel.RunObsidianBridgeScanAsync);

            Assert.Equal(0, imported);

            // llm-tasks/ should not have been created (or should be empty).
            var taskDir = Path.Combine(repoRoot, "llm-tasks");
            if (Directory.Exists(taskDir))
            {
                Assert.Empty(Directory.GetDirectories(taskDir));
            }

            // Source file should still be in New Tasks — untouched.
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Cleanup(vaultRoot, repoRoot);
        }
    }

    // ── Auto-run suppressed by Pause ──────────────────────────────────

    [AvaloniaFact]
    public async Task RunObsidianBridgeScanAsync_WhenPaused_ImportsButDoesNotAutoRun()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();

            // Pause the queue.
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.PauseRequested = true);

            var newTasksDir = Path.Combine(vaultRoot, RootFolderDisplay.Name(repoRoot), "New Tasks");
            Directory.CreateDirectory(newTasksDir);

            var sourcePath = Path.Combine(newTasksDir, "paused-import.md");
            File.WriteAllText(sourcePath, "# Paused\n\nImport only.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            // Scan should still import (not suppressed by pause).
            var imported = await Dispatcher.UIThread.InvokeAsync(
                viewModel.RunObsidianBridgeScanAsync);

            // Import should succeed — pause only suppresses auto-run, not import.
            Assert.True(imported >= 1);

            // The task should exist in llm-tasks/.
            var taskDir = Path.Combine(repoRoot, "llm-tasks");
            Assert.NotEmpty(Directory.GetDirectories(taskDir));

            // Source should be moved to Recognized/.
            Assert.False(File.Exists(sourcePath));
        }
        finally
        {
            Cleanup(vaultRoot, repoRoot);
        }
    }

    // ── Bridge tick no-ops while IsBusy ────────────────────────────────

    [AvaloniaFact]
    public async Task RunObsidianBridgeScanAsync_NoOpsWhenBusy()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();

            // Mark the VM as busy (simulating a running drain/task).
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.IsBusy = true);

            var newTasksDir = Path.Combine(vaultRoot, RootFolderDisplay.Name(repoRoot), "New Tasks");
            Directory.CreateDirectory(newTasksDir);

            var sourcePath = Path.Combine(newTasksDir, "busy-ignore.md");
            File.WriteAllText(sourcePath, "# Busy\n\nShould be skipped.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var imported = await Dispatcher.UIThread.InvokeAsync(
                viewModel.RunObsidianBridgeScanAsync);

            // While busy, the bridge must not scan/import.
            Assert.Equal(0, imported);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Cleanup(vaultRoot, repoRoot);
        }
    }
}
