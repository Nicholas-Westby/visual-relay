using Avalonia.Media;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
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
    public IBrush PauseButtonBackground => PauseRequested ? PauseActiveBackground : PauseIdleBackground;
    public IBrush PauseButtonBorderBrush => PauseRequested ? PauseActiveBorder : PauseIdleBorder;
    public IBrush PauseButtonForeground => PauseRequested ? PauseActiveForeground : PauseIdleForeground;
    public bool IsViewingDifferentTaskDuringRun =>
        _runningTaskId is not null && SelectedTask is not null && !string.Equals(SelectedTask.Id, _runningTaskId, StringComparison.Ordinal);
    public string ViewingRunContextText => IsViewingDifferentTaskDuringRun ? $"Viewing {SelectedTask!.Id} · running {_runningTaskId}" : string.Empty;
}
