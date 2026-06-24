using VisualRelay.App.ViewModels;
using VisualRelay.Core.ObsidianBridge;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// FIX 3: a drain-completed task's published Obsidian summary must carry the real
/// commit SHA and flag reason. The drain hook previously hard-coded
/// CommitSha/Reason to null (passing only the status), so a drained task's summary
/// showed no commit hash and no flag reason — unlike the single-run path which
/// passes the full outcome. Drives the internal drain lifecycle hook directly.
///
/// A sandboxed <see cref="DictionaryEnvironmentAccessor"/> (temp HOME) is injected
/// so toggling the bridge settings persists into a throwaway config, never the
/// user's real ~/.config/visual-relay/.env.
/// </summary>
[Collection("Headless")]
public sealed class ObsidianDrainSummaryTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(),
        "vr-obsidian-drain", Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFileSystem.DeleteDirectoryResilient(_scratch);

    private MainWindowViewModel CreateViewModel(TestRepository repo)
    {
        var env = new DictionaryEnvironmentAccessor
        {
            ["HOME"] = Path.Combine(_scratch, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(_scratch, "xdg")
        };
        Directory.CreateDirectory(env["HOME"]!);
        return new MainWindowViewModel(environmentAccessor: env) { RootPath = repo.Root };
    }

    [AvaloniaFact]
    public async Task DrainCommittedTask_SummaryIncludesRealCommitSha()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("ship-it", "# Ship It\n\nDo the thing.");

        var vaultRoot = Path.Combine(_scratch, "vault");

        var viewModel = CreateViewModel(repo);
        await viewModel.LoadInitialAsync();
        viewModel.ObsidianVaultRoot = vaultRoot;
        viewModel.ObsidianEnabled = true;

        var callbacks = viewModel.CreateDrainLifecycleCallbacks();
        callbacks.OnExecuteStarted!("ship-it");

        // The drain completes the task with a real commit SHA — the hook must forward it.
        var outcome = new RelayTaskOutcome(
            "ship-it", RelayTaskOutcomeStatus.Committed, "hash", "abc1234def", null);
        callbacks.OnExecuteCompleted!("ship-it", outcome);

        var summary = await WaitForSummaryAsync(viewModel, vaultRoot, "ship-it");

        Assert.Contains("vr-commit: abc1234def", summary, StringComparison.Ordinal);
        Assert.Contains("abc1234def", summary, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task DrainFlaggedTask_SummaryIncludesReason()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("needs-eyes", "# Needs Eyes\n\nReview me.");

        var vaultRoot = Path.Combine(_scratch, "vault2");

        var viewModel = CreateViewModel(repo);
        await viewModel.LoadInitialAsync();
        viewModel.ObsidianVaultRoot = vaultRoot;
        viewModel.ObsidianEnabled = true;

        var callbacks = viewModel.CreateDrainLifecycleCallbacks();
        callbacks.OnExecuteStarted!("needs-eyes");

        const string reason = "commit rejected: lint failure on line 42";
        var outcome = new RelayTaskOutcome(
            "needs-eyes", RelayTaskOutcomeStatus.Flagged, "hash", null, reason);
        callbacks.OnExecuteCompleted!("needs-eyes", outcome);

        var summary = await WaitForSummaryAsync(viewModel, vaultRoot, "needs-eyes");

        Assert.Contains(reason, summary, StringComparison.Ordinal);
        Assert.Contains("needs-review", summary, StringComparison.Ordinal);
    }

    private static async Task<string> WaitForSummaryAsync(
        MainWindowViewModel viewModel, string vaultRoot, string taskId)
    {
        var repoName = await ObsidianVaultLayout.ResolveProjectFolderNameAsync(
            viewModel.RootPath);
        var layout = new ObsidianVaultLayout(vaultRoot, repoName);
        // The export is fire-and-forget (_ = ExportSummaryOnCompletion(...)). Poll a
        // short while for the dated summary to appear under Completed/<date>/.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var hit = FindSummary(layout, taskId);
            if (hit is not null) return await File.ReadAllTextAsync(hit);
            await Task.Delay(50);
        }

        var dirs = Directory.Exists(layout.RepoDir)
            ? string.Join(", ", Directory.GetFileSystemEntries(
                layout.RepoDir, "*", SearchOption.AllDirectories))
            : "(no repo dir)";
        throw new Xunit.Sdk.XunitException(
            $"Summary for '{taskId}' never appeared under {layout.RepoDir}. Tree: {dirs}");
    }

    private static string? FindSummary(ObsidianVaultLayout layout, string taskId)
    {
        var completedRoot = Path.Combine(layout.RepoDir, "Completed");
        if (!Directory.Exists(completedRoot)) return null;
        return Directory
            .EnumerateFiles(completedRoot, $"{taskId}.md", SearchOption.AllDirectories)
            .FirstOrDefault();
    }
}
