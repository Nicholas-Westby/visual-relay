using Avalonia;
using Avalonia.Media;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public sealed class TaskRowViewModel : ViewModelBase
{
    private static readonly IBrush MutedBrush = Brush.Parse("#7F8794");
    private static readonly IBrush SelectedBrush = Brush.Parse("#3191FF");
    private static readonly IBrush RunningBrush = Brush.Parse("#5AD47D");
    private static readonly IBrush ReviewBrush = Brush.Parse("#F2C66D");
    private static readonly IBrush WaitingCardBrush = Brush.Parse("#171A20");
    private static readonly IBrush SelectedCardBrush = Brush.Parse("#16233D");
    private static readonly IBrush RunningCardBrush = Brush.Parse("#14231B");
    private static readonly IBrush WaitingBorderBrush = Brush.Parse("#2A303A");
    private static readonly IBrush SelectedBorderBrush = Brush.Parse("#3191FF");
    private static readonly IBrush RunningBorderBrush = Brush.Parse("#5AD47D");
    private static readonly BoxShadows NoShadow = BoxShadows.Parse("0 0 0 0 #00000000");
    private static readonly BoxShadows SelectedShadow = BoxShadows.Parse("0 0 22 0 #553F8CFF");
    private static readonly BoxShadows RunningShadow = BoxShadows.Parse("0 0 22 0 #445AD47D");

    private bool _isSelected;
    private bool _isRunning;
    private int? _runningStageNumber;
    private string? _runningStageName;

    public TaskRowViewModel(RelayTaskItem task)
    {
        Task = task;
    }

    public RelayTaskItem Task { get; }
    public string Id => Task.Id;
    public string MarkdownPath => Task.MarkdownPath;
    public string ReviewReason => Task.ReviewReason ?? string.Empty;
    public bool NeedsReview => Task.NeedsReview;
    public bool IsArchived => Task.IsArchived;
    public string StateLabel => IsRunning ? "Running" : Task.StateLabel;
    public string MetricsLine => IsRunning ? RunningStepLabel : Task.MetricsLine;
    public string RunningStepLabel => _runningStageNumber is { } number
        ? $"Stage {number:00} · {_runningStageName ?? "Running"}"
        : "Starting task";
    public IBrush AccentBrush => IsRunning ? RunningBrush : NeedsReview ? ReviewBrush : SelectedBrush;
    public IBrush RailBrush => IsRunning ? RunningBrush : IsSelected ? SelectedBrush : Brushes.Transparent;
    public IBrush CardBackgroundBrush => IsRunning ? RunningCardBrush : IsSelected ? SelectedCardBrush : WaitingCardBrush;
    public IBrush BorderBrush => IsRunning ? RunningBorderBrush : IsSelected ? SelectedBorderBrush : WaitingBorderBrush;
    public Thickness CardBorderThickness => IsRunning || IsSelected ? new Thickness(2) : new Thickness(1);
    public BoxShadows CardShadow => IsRunning ? RunningShadow : IsSelected ? SelectedShadow : NoShadow;
    public string ProgressText => Task.CompletedStageCount == 0 ? "0 / 11" : $"{Task.CompletedStageCount} / 11";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                NotifyVisualStateChanged();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(MetricsLine));
                NotifyVisualStateChanged();
            }
        }
    }

    public void MarkRunning(int? stageNumber = null, string? stageName = null)
    {
        _runningStageNumber = stageNumber;
        _runningStageName = stageName;
        IsRunning = true;
        OnPropertyChanged(nameof(RunningStepLabel));
        OnPropertyChanged(nameof(MetricsLine));
    }

    public void MarkIdle()
    {
        _runningStageNumber = null;
        _runningStageName = null;
        IsRunning = false;
        OnPropertyChanged(nameof(RunningStepLabel));
        OnPropertyChanged(nameof(MetricsLine));
    }

    private void NotifyVisualStateChanged()
    {
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(RailBrush));
        OnPropertyChanged(nameof(CardBackgroundBrush));
        OnPropertyChanged(nameof(BorderBrush));
        OnPropertyChanged(nameof(CardBorderThickness));
        OnPropertyChanged(nameof(CardShadow));
    }
}
