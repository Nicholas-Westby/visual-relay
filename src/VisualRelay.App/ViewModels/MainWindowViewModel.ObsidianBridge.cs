using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Configuration;
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
        try
        {
            ObsidianBridgeSettings.Save(new ObsidianBridgeConfig(
                ObsidianEnabled, ObsidianVaultRoot, ObsidianPollSeconds), EnvironmentAccessor);
        }
        catch { /* best-effort */ }
    }

    private void LoadObsidianBridgeSettings()
    {
        try
        {
            var config = ObsidianBridgeSettings.Load(EnvironmentAccessor);
            ObsidianEnabled = config.Enabled;
            ObsidianVaultRoot = config.VaultRoot;
            ObsidianPollSeconds = config.PollSeconds;
        }
        catch { /* best-effort */ }
    }

    [RelayCommand]
    private async Task BrowseVaultRootAsync()
    {
        var folder = await _folderPicker.PickFolderAsync();
        if (folder is not null) ObsidianVaultRoot = folder;
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
            var repoName = await ObsidianVaultLayout.ResolveProjectFolderNameAsync(RootPath);
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
        foreach (var task in (await repository.ListCompletedAsync()).Take(50))
        {
            if (string.IsNullOrWhiteSpace(task.MarkdownPath) || !File.Exists(task.MarkdownPath))
                continue;

            var metric = RelayRunHistory.ReadTaskMetric(RootPath, task.Id);
            var date = metric.Stages.Count > 0
                ? DateOnly.FromDateTime(metric.Stages.Max(s => s.Timestamp).Date)
                : DateOnly.FromDateTime(DateTime.UtcNow.Date);

            if (File.Exists(layout.SummaryPath(task.Id, date))) continue;

            var spec = await File.ReadAllTextAsync(task.MarkdownPath);
            writer.Write(layout, RootPath, task.Id, null, spec, null, DateTimeOffset.UtcNow);
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
            var repoName = await ObsidianVaultLayout.ResolveProjectFolderNameAsync(RootPath);
            var layout = new ObsidianVaultLayout(ObsidianVaultRoot, repoName);
            layout.EnsureScaffold();
            var spec = await ResolveTaskSpecAsync(taskId);
            new ObsidianSummaryWriter().Write(layout, RootPath, taskId, outcome, spec, sourceGuid, DateTimeOffset.UtcNow);
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
