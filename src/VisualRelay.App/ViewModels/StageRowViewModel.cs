using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public sealed class StageRowViewModel : ViewModelBase
{
    private static readonly IBrush MutedBrush = Brush.Parse("#7F8794");
    private static readonly IBrush SuccessBrush = Brush.Parse("#5AD47D");
    private static readonly IBrush RunningBrush = Brush.Parse("#5AD47D");
    private static readonly IBrush FlaggedBrush = Brush.Parse("#F36F63");
    private static readonly IBrush WaitingCardBrush = Brush.Parse("#171A20");
    private static readonly IBrush ActiveCardBrush = Brush.Parse("#14231B");
    private static readonly IBrush SelectedCardBrush = Brush.Parse("#162B57");
    private static readonly IBrush WaitingBorderBrush = Brush.Parse("#2A303A");
    private static readonly IBrush SelectedBorderBrush = Brush.Parse("#3191FF");
    private static readonly BoxShadows NoShadow = BoxShadows.Parse("0 0 0 0 #00000000");
    private static readonly BoxShadows SelectedShadow = BoxShadows.Parse("0 0 20 0 #553F8CFF");
    private static readonly BoxShadows RunningShadow = BoxShadows.Parse("0 0 20 0 #445AD47D");

    public StageRowViewModel(RelayStageDefinition stage, IRelayCommand<StageRowViewModel>? selectCommand = null)
    {
        Number = stage.Number;
        Name = stage.Name;
        Tier = stage.Tier;
        Status = "Waiting";
        SelectCommand = selectCommand;
    }

    public int Number { get; }
    public string Name { get; }
    public string Tier { get; }
    public IRelayCommand<StageRowViewModel>? SelectCommand { get; }
    public string Ordinal => Number.ToString("00");
    public string StatusLabel => Status == "Done" ? "Complete" : Status;
    public string MetricLabel => (CostLabel == "No cost yet" ? DurationLabel : $"{DurationLabel}  {CostLabel}")
        + (string.IsNullOrEmpty(TurnsLabel) ? string.Empty : $"  {TurnsLabel}");
    public IBrush AccentBrush => Status switch
    {
        "Done" => SuccessBrush,
        "Running" => RunningBrush,
        "Flagged" => FlaggedBrush,
        _ => MutedBrush
    };
    public IBrush CardBackgroundBrush => Status == "Running" ? ActiveCardBrush : IsSelected ? SelectedCardBrush : WaitingCardBrush;
    public IBrush BorderBrush => Status == "Running" ? RunningBrush : IsSelected ? SelectedBorderBrush : WaitingBorderBrush;
    public Thickness CardBorderThickness => Status == "Running" || IsSelected ? new Thickness(2) : new Thickness(1);
    public BoxShadows CardShadow => Status == "Running" ? RunningShadow : IsSelected ? SelectedShadow : NoShadow;

    private string _status = "Waiting";
    private bool _isSelected;
    private string _durationLabel = "No run yet";
    private string _costLabel = "No cost yet";
    private string _modelLabel = string.Empty;
    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(AccentBrush));
                OnPropertyChanged(nameof(CardBackgroundBrush));
                OnPropertyChanged(nameof(BorderBrush));
                OnPropertyChanged(nameof(CardBorderThickness));
                OnPropertyChanged(nameof(CardShadow));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(CardBackgroundBrush));
                OnPropertyChanged(nameof(BorderBrush));
                OnPropertyChanged(nameof(CardBorderThickness));
                OnPropertyChanged(nameof(CardShadow));
            }
        }
    }

    public string DurationLabel
    {
        get => _durationLabel;
        set
        {
            if (SetProperty(ref _durationLabel, value))
            {
                OnPropertyChanged(nameof(MetricLabel));
            }
        }
    }

    public string CostLabel
    {
        get => _costLabel;
        set
        {
            if (SetProperty(ref _costLabel, value))
            {
                OnPropertyChanged(nameof(MetricLabel));
            }
        }
    }

    public string ModelLabel
    {
        get => _modelLabel;
        set => SetProperty(ref _modelLabel, value);
    }

    private string _turnsLabel = string.Empty;
    public string TurnsLabel
    {
        get => _turnsLabel;
        set
        {
            if (SetProperty(ref _turnsLabel, value))
            {
                OnPropertyChanged(nameof(MetricLabel));
            }
        }
    }

    public string? ReportPath { get; private set; }
    public string? TraceDirectory { get; private set; }

    // Prefer the report (always exists for a run); fall back to the trace dir.
    public string? RevealTarget => ReportPath ?? TraceDirectory;

    public void ApplyMetric(StageRunMetric metric)
    {
        DurationLabel = metric.DurationLabel;
        CostLabel = metric.CostLabel;
        ModelLabel = metric.Model;
        TurnsLabel = metric.Turns > 0 ? $"{metric.Turns}t" : string.Empty;
        ReportPath = metric.ReportPath;
        TraceDirectory = metric.TraceDirectory;
    }

    public void ClearMetric()
    {
        DurationLabel = "No run yet";
        CostLabel = "No cost yet";
        ModelLabel = string.Empty;
        TurnsLabel = string.Empty;
        ReportPath = null;
        TraceDirectory = null;
    }
}
