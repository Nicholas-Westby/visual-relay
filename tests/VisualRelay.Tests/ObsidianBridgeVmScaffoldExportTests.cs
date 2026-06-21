using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// Scaffold and export reconciliation tests for the Obsidian bridge.
/// Split from <see cref="ObsidianBridgeVmTests"/> to stay under the 300-line guard.
/// </summary>
[Collection("Headless")]
public sealed class ObsidianBridgeVmScaffoldExportTests : IDisposable
{
    private readonly string _tempHome;

    public ObsidianBridgeVmScaffoldExportTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "vr-obsidian-vm-scaf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
    }

    public void Dispose()
    {
        TestFileSystem.DeleteDirectoryResilient(_tempHome);
    }

    private static (string VaultRoot, string RepoRoot) SetupDirs()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "vr-obsidian-vm-scaf-tests",
            Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(Path.GetTempPath(), "vr-obsidian-vm-scaf-repo",
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
        var configDir = Path.Combine(repoRoot, ".relay");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"),
            """{"testCmd": "true", "logSources": []}""");

        env["HOME"] = _tempHome;
        var settingsDir = Path.Combine(_tempHome, ".config", "visual-relay");
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, "obsidian.json"),
            $$"""{"enabled": {{(bridgeEnabled ? "true" : "false")}}, "vaultRoot": "{{vaultRoot.Replace("\\", "/")}}", "pollSeconds": 60}""");

        return new MainWindowViewModel(environmentAccessor: env) { RootPath = repoRoot };
    }

    [AvaloniaFact]
    public async Task RunObsidianBridgeScanAsync_CreatesScaffoldOnFirstRun()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();

            var repoName = RootFolderDisplay.Name(repoRoot);
            var repoDir = Path.Combine(vaultRoot, repoName);
            Assert.False(Directory.Exists(repoDir),
                "Vault repo dir should not exist before first scan");

            await Dispatcher.UIThread.InvokeAsync(
                viewModel.RunObsidianBridgeScanAsync);

            Assert.True(Directory.Exists(repoDir));
            Assert.True(Directory.Exists(Path.Combine(repoDir, "New Tasks")));
            Assert.True(Directory.Exists(Path.Combine(repoDir, "New Tasks", "Recognized")));
            Assert.True(Directory.Exists(Path.Combine(repoDir, "Completed")));
            Assert.True(File.Exists(Path.Combine(repoDir, "INFO.md")));
        }
        finally { Cleanup(vaultRoot, repoRoot); }
    }

    [AvaloniaFact]
    public async Task RunObsidianBridgeScanAsync_ExportsSummariesForCompletedTasks()
    {
        var (vaultRoot, repoRoot) = SetupDirs();
        var env = new DictionaryEnvironmentAccessor();
        try
        {
            var taskId = "done-task";
            var nestedDir = Path.Combine(repoRoot, "llm-tasks", taskId);
            Directory.CreateDirectory(nestedDir);
            File.WriteAllText(Path.Combine(nestedDir, $"{taskId}.md"), "# Done\n\nAlready finished.");
            File.WriteAllText(Path.Combine(repoRoot, "llm-tasks", $"DONE-{taskId}.md"), "# Done");

            var relayDir = Path.Combine(repoRoot, ".relay", taskId);
            Directory.CreateDirectory(relayDir);
            File.WriteAllText(Path.Combine(relayDir, "stage1-attempt1.report.json"),
                """{"timestamp": "2026-06-20T14:00:00+00:00", "model": "cheap", "result": {"outcome": "success"}, "stats": {"total_llm_time_s": 1.0}, "timeline": [{"type": "llm_call", "prompt_tokens_est": 100}]}""");
            File.WriteAllText(Path.Combine(relayDir, "status.json"),
                """[{"stage": 1, "name": "Ideate", "status": "Done"}]""");

            var viewModel = CreateViewModel(repoRoot, vaultRoot, env);
            await viewModel.LoadInitialAsync();
            await Dispatcher.UIThread.InvokeAsync(viewModel.RunObsidianBridgeScanAsync);

            var repoName = RootFolderDisplay.Name(repoRoot);
            var summaryPath = Path.Combine(vaultRoot, repoName, "Completed", "2026-06-20", $"{taskId}.md");
            Assert.True(File.Exists(summaryPath));
            var content = File.ReadAllText(summaryPath);
            Assert.Contains("done-task", content, StringComparison.Ordinal);
        }
        finally { Cleanup(vaultRoot, repoRoot); }
    }
}
