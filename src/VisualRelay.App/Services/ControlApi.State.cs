using Avalonia.Threading;
using VisualRelay.Core.Queue;

namespace VisualRelay.App.Services;

public sealed partial class ControlApi
{
    /// <summary>
    /// Builds the /state JSON snapshot ON THE UI THREAD. Mirrors the user-visible
    /// state: root/archive/busy/pause/status, backend reachability, the selected
    /// task, the task list, the stage board, and a per-command enabled map
    /// computed from each command's CanExecute (the same gate the UI buttons use).
    /// When <paramref name="instanceId"/> is non-null it is included as a top-level
    /// field so every state response carries instance identity.
    /// </summary>
    public Task<string> BuildStateJsonAsync(string? instanceId = null) =>
        Dispatcher.UIThread.InvokeAsync(() => Json.Serialize(BuildStateSnapshot(instanceId))).GetTask();

    private object BuildStateSnapshot(string? instanceId = null)
    {
        var vm = viewModel;
        return new
        {
            instanceId,
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
            setupCheck = vm.SetupCheck is { } sc ? new
            {
                command = sc.Command,
                cwd = sc.Cwd,
                timeoutMs = sc.TimeoutMs,
                exitCode = sc.ExitCode,
                timedOut = sc.TimedOut,
                outputTail = sc.OutputTail,
                artifactPath = sc.ArtifactPath,
                capturedUtc = sc.CapturedUtc,
                hint = sc.Hint
            } : null,
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
            },
            runAllMode = vm.SelectedRunAllMode.ToString(),
            pendingHandoff = RestartHandoff.Read(vm.RootPath) is not null
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
        map["boost-turns"] = new { enabled = viewModel.SelectedTask is not null };
        map["skip-tests"] = new { enabled = viewModel.SelectedTask is not null };
        map["open-folder"] = new { enabled = true };
        map["obsidian-scan"] = new { enabled = viewModel is { ObsidianEnabled: true, IsBusy: false } };
        map["obsidian-bridge"] = new { enabled = true };
        // Tab navigation is always available — switching tabs has no precondition.
        map["select-activity-tab"] = new { enabled = true };
        map["select-detail-tab"] = new { enabled = true };

        return map;
    }

    private static readonly string[] IcommandNames =
    [
        "bootstrap", "run-all", "run-selected", "resume", "refresh", "pause-toggle",
        "archive-toggle", "new-task", "follow-running", "start-backend", "edit",
        "rewrite-selected", "cancel-rewrite", "revert-rewrite", "mark-done", "reset-selected"
    ];

    /// <summary>
    /// Ordered list of every documented command name — ICommand-backed actions
    /// first (from IcommandNames), then property-backed actions (from
    /// PropertyActions). The index page renders this list; adding a command to
    /// either source array automatically flows here and onto the page.
    /// Computed on access rather than a cached initializer: PropertyActions lives
    /// in the other partial-class part, and static field-initializer order across
    /// partial parts is unspecified, so a cached initializer could observe a
    /// not-yet-initialized (null) PropertyActions. Evaluating on access runs after
    /// all static fields are set, so both source arrays are always populated.
    /// </summary>
    public static IReadOnlyList<string> CommandNames => [.. IcommandNames, .. PropertyActions];
}
