using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.ObsidianBridge;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool _obsidianEnabled;

    [ObservableProperty]
    private string _obsidianVaultRoot = string.Empty;

    [ObservableProperty]
    private int _obsidianPollSeconds = 60;

    private bool _bridgeCycleBusy;
    private DispatcherTimer? _obsidianBridgeTimer;

    /// <summary>
    /// Distinguishes the writer of bridge settings for the audit log.
    /// Defaults to <c>"settings-ui"</c>; the control API sets it to
    /// <c>"control-api"</c> before mutating properties.
    /// </summary>
    internal string BridgeSettingsSource { get; set; } = "settings-ui";

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnObsidianEnabledChanged(bool value) => PersistBridgeSettings();
    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnObsidianVaultRootChanged(string value) => PersistBridgeSettings();

    partial void OnObsidianPollSecondsChanged(int value)
    {
        // Clamp at the live-set boundary too — Load alone leaves a value pushed via
        // the settings TextBox or the control API able to spin the timer far too
        // fast. Reassigning the clamped value re-enters this handler once and then
        // no-ops (clamped == clamped), so persistence runs exactly once at the floor.
        if (value < ObsidianBridgeSettings.MinPollSeconds)
        {
            ObsidianPollSeconds = ObsidianBridgeSettings.MinPollSeconds;
            return;
        }

        PersistBridgeSettings();
    }

    private void PersistBridgeSettings()
    {
        if (_isHydrating) return;

        try
        {
            ObsidianBridgeSettings.Save(new ObsidianBridgeConfig(
                ObsidianEnabled, ObsidianVaultRoot, ObsidianPollSeconds),
                EnvironmentAccessor, BridgeSettingsSource);
            StatusText = "Obsidian bridge settings saved";
        }
        catch { /* best-effort */ }
    }

    private void LoadObsidianBridgeSettings()
    {
        try
        {
            _isHydrating = true;
            var config = ObsidianBridgeSettings.Load(EnvironmentAccessor);
            ObsidianEnabled = config.Enabled;
            ObsidianVaultRoot = config.VaultRoot;
            ObsidianPollSeconds = config.PollSeconds;
        }
        catch { /* best-effort */ }
        finally
        {
            _isHydrating = false;
        }
    }

    [RelayCommand]
    private async Task BrowseVaultRootAsync()
    {
        var folder = await _folderPicker.PickFolderAsync();
        if (folder is not null) ObsidianVaultRoot = folder;
    }

    [RelayCommand]
    private void RevealVaultRoot()
    {
        if (!string.IsNullOrWhiteSpace(ObsidianVaultRoot))
            FileReveal.Reveal(TildePath.Expand(ObsidianVaultRoot));
    }

    /// <summary>
    /// Runs one bridge scan cycle: imports stable files from <c>New Tasks/</c>
    /// and reconciles exports for completed tasks. Returns import count.
    /// Best-effort: vault errors never break a run.
    /// </summary>
    internal async Task<int> RunObsidianBridgeScanAsync()
    {
        if (!ObsidianEnabled || string.IsNullOrWhiteSpace(RootPath) || !Directory.Exists(RootPath))
            return 0;
        if (IsBusy || _runningTaskIds.Count > 0 || IsSettingsOpen || IsEditingMarkdown || IsNewTaskDialogOpen)
            return 0;
        if (_bridgeCycleBusy) return 0;

        _bridgeCycleBusy = true;
        try
        {
            var repoName = await ObsidianVaultLayout.ResolveProjectFolderNameAsync(RootPath, new GitInvoker());
            var layout = new ObsidianVaultLayout(ObsidianVaultRoot, repoName);
            layout.EnsureScaffold();

            var imported = 0;
            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            foreach (var candidate in candidates)
            {
                try
                {
                    var result = await importer.Recognize(candidate, RootPath, DateTimeOffset.UtcNow, Guid.NewGuid());
                    if (result.Slug is not null) imported++;
                    else if (result.SkipReason is not null)
                        StatusText = $"Obsidian: skipped \"{candidate.Title}\" — {result.SkipReason}";
                }
                catch (Exception ex) { StatusText = $"Obsidian import error: {ex.Message}"; }
            }

            if (imported > 0)
                await ReloadTaskListAsync();

            try { await ReconcileExportsAsync(layout); } catch (Exception ex) { StatusText = $"Obsidian export error: {ex.Message}"; }
            return imported;
        }
        catch (Exception ex) { StatusText = $"Obsidian bridge error: {ex.Message}"; return 0; }
        finally { _bridgeCycleBusy = false; }
    }

    private async Task ReconcileExportsAsync(ObsidianVaultLayout layout)
    {
        var repository = new RelayTaskRepository(RootPath);
        var writer = new ObsidianSummaryWriter();
        var ledger = new ExportLedger(layout.RepoDir);
        var completed = await repository.ListCompletedAsync();

        // First-scan seeding.
        var completedIds = completed
            .Select(t => t.Id)
            .Where(ObsidianVaultLayout.IsValidTaskId)
            .ToArray();
        var hasNotes = HasCompletedNotes(layout);
        var (decision, _) = await ledger.TrySeedAsync(completedIds, hasNotes);

        if (decision == SeedDecision.SealOnly) return;

        if (decision == SeedDecision.FullBackfill)
        {
            foreach (var task in completed)
            {
                if (string.IsNullOrWhiteSpace(task.MarkdownPath) || !File.Exists(task.MarkdownPath))
                    continue;
                var metric = RelayRunHistory.ReadTaskMetric(RootPath, task.Id);
                if (metric.Stages.Count == 0) continue;
                var spec = await File.ReadAllTextAsync(task.MarkdownPath);
                writer.Write(layout, RootPath, task.Id, null, spec, null, DateTimeOffset.UtcNow);
                await ledger.RecordAsync(task.Id);
            }
            return;
        }

        // Normal gated top-50 loop.
        foreach (var task in completed.Take(50))
        {
            if (string.IsNullOrWhiteSpace(task.MarkdownPath) || !File.Exists(task.MarkdownPath))
                continue;
            var metric = RelayRunHistory.ReadTaskMetric(RootPath, task.Id);
            if (metric.Stages.Count == 0) continue;
            if (await ledger.ContainsAsync(task.Id)) continue;
            var spec = await File.ReadAllTextAsync(task.MarkdownPath);
            writer.Write(layout, RootPath, task.Id, null, spec, null, DateTimeOffset.UtcNow);
            await ledger.RecordAsync(task.Id);
        }
    }

    /// <summary>
    /// Exports a run summary to the vault when a task completes. Best-effort.
    /// </summary>
    private async Task ExportSummaryOnCompletion(string taskId, RelayTaskOutcome outcome, Guid? sourceGuid = null)
    {
        if (!ObsidianEnabled || string.IsNullOrWhiteSpace(ObsidianVaultRoot)) return;
        try
        {
            var repoName = await ObsidianVaultLayout.ResolveProjectFolderNameAsync(RootPath, new GitInvoker());
            var layout = new ObsidianVaultLayout(ObsidianVaultRoot, repoName);
            layout.EnsureScaffold();
            var spec = await ResolveTaskSpecAsync(taskId);
            new ObsidianSummaryWriter().Write(layout, RootPath, taskId, outcome, spec, sourceGuid, DateTimeOffset.UtcNow);
            await new ExportLedger(layout.RepoDir).RecordAsync(taskId);
        }
        catch { /* best-effort */ }
    }

    private async Task<string> ResolveTaskSpecAsync(string taskId)
    {
        try
        {
            var repo = new RelayTaskRepository(RootPath);
            // Look in completed first (most likely for an already-retired task).
            var completed = await repo.ListCompletedAsync();
            var match = completed.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
            if (match is not null && File.Exists(match.MarkdownPath))
                return await File.ReadAllTextAsync(match.MarkdownPath);

            // Fall back to pending tasks.
            var pending = await repo.ListAsync();
            match = pending.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
            if (match is not null && File.Exists(match.MarkdownPath))
                return await File.ReadAllTextAsync(match.MarkdownPath);
        }
        catch { /* best-effort: fall back to placeholder spec */ }
        return $"# {taskId}\n\n(Spec unavailable)";
    }

    /// <summary>
    /// Starts the bridge polling timer. Called ONLY from App startup so tests spin no timer.
    /// </summary>
    public void StartObsidianBridge()
    {
        _obsidianBridgeTimer?.Stop();
        _obsidianBridgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ObsidianPollSeconds) };
        _obsidianBridgeTimer.Tick += (_, _) => _ = OnBridgeTickAsync();
        _obsidianBridgeTimer.Start();
    }

    /// <summary>
    /// Returns true when the vault already shows signs of prior exports:
    /// any <c>.md</c> notes (excluding INFO/README) or dated subdirectories
    /// (<c>YYYY-MM-DD</c>) left behind after note deletion.
    /// </summary>
    private static bool HasCompletedNotes(ObsidianVaultLayout layout)
    {
        var completedRoot = Path.Combine(layout.RepoDir, "Completed");
        if (!Directory.Exists(completedRoot)) return false;

        // Actual task notes (not scaffold INFO/README).
        if (Directory.EnumerateFiles(completedRoot, "*.md", SearchOption.AllDirectories)
            .Any(f => !ObsidianVaultLayout.ReservedFileNames.Contains(
                Path.GetFileName(f))))
            return true;

        // Dated subdirectories left behind after note deletion.
        return Directory.EnumerateDirectories(completedRoot)
            .Any(d => System.Text.RegularExpressions.Regex.IsMatch(
                Path.GetFileName(d), @"^\d{4}-\d{2}-\d{2}$"));
    }

    private async Task OnBridgeTickAsync()
    {
        if (!ObsidianEnabled || IsBusy || _runningTaskIds.Count > 0 ||
            IsSettingsOpen || IsEditingMarkdown || IsNewTaskDialogOpen || _bridgeCycleBusy)
            return;
        var imported = await RunObsidianBridgeScanAsync();
        if (imported > 0 && !PauseRequested && CanDrain())
            await DrainQueueCommand.ExecuteAsync(null);
    }
}
