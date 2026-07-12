using Avalonia.Media;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    public RunAllMode SelectedRunAllMode { get; set; } = RunAllMode.Standard;

    public static IReadOnlyList<RunAllMode> RunAllModeOptions { get; } =
        [RunAllMode.Standard, RunAllMode.Sequential];
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
