using Avalonia.Threading;

namespace VisualRelay.App.Services;

public sealed partial class ControlApi
{
    /// <summary>
    /// Builds the /state JSON snapshot ON THE UI THREAD. Mirrors the user-visible
    /// state: root/archive/busy/pause/status, backend reachability, the selected
    /// task, the task list, the stage board, and a per-command enabled map
    /// computed from each command's CanExecute (the same gate the UI buttons use).
    /// </summary>
    public Task<string> BuildStateJsonAsync() =>
        Dispatcher.UIThread.InvokeAsync(() => Json.Serialize(BuildStateSnapshot())).GetTask();

    private object BuildStateSnapshot()
    {
        var vm = viewModel;
        return new
        {
            rootPath = vm.RootPath,
            showArchive = vm.ShowArchive,
            isBusy = vm.IsBusy,
            pauseRequested = vm.PauseRequested,
            statusText = vm.StatusText,
            backend = new
            {
                reachable = vm.IsBackendReachable,
                label = vm.BackendStatusLabel,
                message = vm.BackendStatusMessage
            },
            selectedTask = BuildSelectedTask(),
            tasks = vm.Tasks.Select(t => new
            {
                id = t.Id,
                stateLabel = t.StateLabel,
                needsReview = t.NeedsReview
            }).ToArray(),
            stages = vm.Stages.Select(s => new
            {
                number = s.Number,
                name = s.Name,
                status = s.Status,
                tier = s.Tier
            }).ToArray(),
            commands = BuildCommandsMap(),
            obsidianBridge = new
            {
                enabled = vm.ObsidianEnabled,
                vaultRoot = vm.ObsidianVaultRoot,
                pollSeconds = vm.ObsidianPollSeconds
            }
        };
    }

    private object? BuildSelectedTask()
    {
        var selected = viewModel.SelectedTask;
        if (selected is null)
        {
            return null;
        }

        return new
        {
            id = selected.Id,
            stateLabel = selected.StateLabel,
            needsReview = selected.NeedsReview,
            reviewReason = string.IsNullOrEmpty(selected.ReviewReason) ? null : selected.ReviewReason,
            metricLabel = viewModel.SelectedTaskMetricLabel,
            error = viewModel.SelectedTaskError
        };
    }

    private Dictionary<string, object> BuildCommandsMap()
    {
        var map = new Dictionary<string, object>(StringComparer.Ordinal);

        // ICommand-backed actions: enabled == CanExecute(null), the exact gate
        // the bound UI button consults.
        foreach (var name in IcommandNames)
        {
            var command = ResolveCommand(name);
            map[name] = new { enabled = command?.CanExecute(null) ?? false };
        }

        // Property-backed actions: encode the documented enablement rules.
        map["select-task"] = new { enabled = viewModel.Tasks.Count > 0 };
        map["bypass-sandbox"] = new { enabled = true };
        map["boost-turns"] = new { enabled = viewModel.SelectedTask is not null };
        map["open-folder"] = new { enabled = true };
        map["obsidian-scan"] = new { enabled = viewModel is { ObsidianEnabled: true, IsBusy: false } };
        map["obsidian-bridge"] = new { enabled = true };

        return map;
    }

    private static readonly string[] IcommandNames =
    [
        "bootstrap", "run-all", "run-selected", "resume", "refresh", "pause-toggle",
        "archive-toggle", "new-task", "follow-running", "start-backend", "edit",
        "rewrite-selected", "cancel-rewrite", "revert-rewrite"
    ];
}
