using Avalonia.Threading;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

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
        var haltReason = DrainCircuitBreaker.ReadHaltReason(vm.RootPath);
        return new
        {
            instanceId,
            nowUtc = DateTimeOffset.UtcNow,
            rootPath = vm.RootPath,
            showArchive = vm.ShowArchive,
            isBusy = vm.IsBusy,
            pauseRequested = vm.PauseRequested,
            // True from a cancel request until the run has finished winding down —
            // the difference between "stopping" and "stopped".
            cancelRequested = vm.CancelRequested,
            statusText = vm.StatusText,
            // True ⇒ testCmd is the no-op placeholder: a green Verify proves nothing.
            testCommandIsPlaceholder = vm.TestCommandIsPlaceholder,
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
            // Activity: what the app is doing right now, and when it last did
            // anything at all. nowUtc minus lastActivityUtc while isBusy is the
            // caller's stuck detector.
            lastActivityUtc = vm.LastActivityUtc,
            lastEvent = BuildLastEvent(),
            runningTasks = vm.RunningTasks().Select(t => new
            {
                taskId = t.TaskId,
                stageNumber = t.StageNumber,
                stageName = t.StageName,
                tier = t.Tier
            }).ToArray(),
            sessionCostUsd = vm.SessionCostUsd,
            drainHalted = haltReason is not null,
            haltReason = Clip(haltReason, 500),
            selectedTask = BuildSelectedTask(),
            tasks = vm.Tasks.Select(t => BuildTask(t.Task)).ToArray(),
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

        // The same per-task projection every tasks[] entry gets, plus the two
        // fields only the selected task has.
        var entry = BuildTask(selected.Task);
        entry["metricLabel"] = viewModel.SelectedTaskMetricLabel;
        entry["error"] = viewModel.SelectedTaskError;
        return entry;
    }

    /// <summary>
    /// Projects one task row: identity and review state plus the run metrics the
    /// record already carries (cost, duration, stage counts), so a caller can
    /// account for a queue without opening any task's files.
    /// </summary>
    private static Dictionary<string, object?> BuildTask(RelayTaskItem task) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = task.Id,
            ["stateLabel"] = task.StateLabel,
            ["needsReview"] = task.NeedsReview,
            ["reviewReason"] = string.IsNullOrEmpty(task.ReviewReason) ? null : task.ReviewReason,
            ["costUsd"] = task.CostUsd,
            ["durationSeconds"] = task.DurationSeconds,
            ["completedStageCount"] = task.CompletedStageCount,
            ["settledStageCount"] = task.SettledStageCount,
            ["pipelineStageCount"] = task.PipelineStageCount
        };

    /// <summary>
    /// The most recent relay event, or null before the first one. The human text
    /// is clipped so a trace event never dumps whole model output into /state.
    /// </summary>
    private object? BuildLastEvent()
    {
        if (viewModel.LastRelayEvent is not { } last)
        {
            return null;
        }

        return new
        {
            utc = last.Timestamp.ToUniversalTime(),
            level = last.Level,
            name = last.EventName,
            taskId = last.TaskId,
            stage = last.StageNumber,
            tier = last.Tier,
            message = Clip(last.DetailLine, 240)
        };
    }

    /// <summary>Truncates to <paramref name="max"/> characters; null stays null.</summary>
    private static string? Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];

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
        // Authoring needs a project folder and a queue that nothing is walking.
        map["create-task"] = new { enabled = !viewModel.IsBusy && Directory.Exists(viewModel.RootPath) };
        // Tab navigation is always available — switching tabs has no precondition.
        map["select-activity-tab"] = new { enabled = true };
        map["select-detail-tab"] = new { enabled = true };

        return map;
    }

    private static readonly string[] IcommandNames =
    [
        "bootstrap", "run-all", "run-selected", "resume", "cancel", "refresh", "pause-toggle",
        "archive-toggle", "new-task", "follow-running", "edit",
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
