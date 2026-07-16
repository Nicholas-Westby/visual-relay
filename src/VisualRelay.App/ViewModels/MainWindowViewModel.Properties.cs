using Avalonia.Media;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    public RunAllMode SelectedRunAllMode { get; set; } = RunAllMode.Standard;

    /// <summary>Display option for the Run All protocol dropdown.</summary>
    public sealed record RunAllModeOption(
        RunAllMode Mode, string Name, string Description);

    public static IReadOnlyList<RunAllModeOption> RunAllModeOptions { get; } =
    [
        new(RunAllMode.Standard,
            "Standard",
            "Plan all tasks up front, then execute"),
        new(RunAllMode.Sequential,
            "Sequential",
            "One task at a time, checking for new tasks between"),
        new(RunAllMode.RestartBetweenTasks,
            "Restart Between Tasks",
            "Sequential, plus the app rebuilds and relaunches after each committed task — for repos that build Visual Relay itself"),
    ];

    /// <summary>
    /// Bridges <see cref="SelectedRunAllMode"/> to the
    /// <see cref="RunAllModeOption"/> selected in the ComboBox.
    /// </summary>
    public RunAllModeOption SelectedRunAllModeOption
    {
        get => RunAllModeOptions.First(o => o.Mode == SelectedRunAllMode);
        set => SelectedRunAllMode = value.Mode;
    }
    public string Version
    {
        get
        {
            var info = VersionHelper.ReadInformationalVersion();
            var plus = info.IndexOf('+');
            return plus >= 0 ? $"v{info[..plus]}" : $"v{info}";
        }
    }

    public string RootName => RootFolderDisplay.Name(RootPath);
    public string RootParentPath => RootFolderDisplay.Parent(RootPath);
    public string WindowTitle => $"Visual Relay - {RootName}";
    public string TaskListTitle => ShowArchive ? "ARCHIVE" : "QUEUE";
    public string TaskListToggleText => ShowArchive ? "Queue" : "Archive";
    public string PauseButtonText => PauseRequested ? "Resume" : "Pause after task";
    public string PauseNoticeText => PauseRequested
        ? IsBusy ? $"Stops after {_runningTaskId ?? "current task"}" : "Paused before next task"
        : string.Empty;
    public bool IsPauseNoticeVisible => PauseRequested;
    public IBrush BackendStatusBrush => IsBackendReachable ? BackendUpBrush : BackendDownBrush;
    public string BackendStatusLabel => IsBackendReachable
        ? $"backend: {new Uri(ModelBackend.BaseUrl).Authority}"
        : "backend down";
    public bool IsViewingDifferentTaskDuringRun =>
        _runningTaskId is not null && SelectedTask is not null && !string.Equals(SelectedTask.Id, _runningTaskId, StringComparison.Ordinal);
    public string ViewingRunContextText => IsViewingDifferentTaskDuringRun ? $"Viewing {SelectedTask!.Id} · running {_runningTaskId}" : string.Empty;
    public bool HasConfigDiagnostic => ConfigDiagnostic is not null;
    public bool HasSetupCheck => SetupCheck is not null;

    /// <summary>
    /// Human-readable diagnostic text rendered in the status flyout when setup check
    /// fails. Built from <see cref="SetupCheck"/> when non-null.
    /// </summary>
    public string SetupCheckDisplay => SetupCheck is not { } sc ? string.Empty
        : $"COMMAND: {sc.Command}\nCWD: {sc.Cwd}\nTIMEOUT: {sc.TimeoutMs / 1000}s\n"
          + $"EXIT CODE: {sc.ExitCode}{(sc.TimedOut ? " (timed out)" : "")}\n"
          + $"HINT: {sc.Hint}\n"
          + (sc.OutputTail is { Length: > 0 } tail ? $"\n--- output ---\n{tail}" : "");
}
