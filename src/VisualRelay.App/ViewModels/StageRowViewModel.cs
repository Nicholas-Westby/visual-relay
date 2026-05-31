using Avalonia.Media;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public sealed class StageRowViewModel : ViewModelBase
{
    private static readonly IBrush MutedBrush = Brush.Parse("#7F8794");
    private static readonly IBrush SuccessBrush = Brush.Parse("#5AD47D");
    private static readonly IBrush RunningBrush = Brush.Parse("#5B7CFA");
    private static readonly IBrush FlaggedBrush = Brush.Parse("#F36F63");
    private static readonly IBrush WaitingCardBrush = Brush.Parse("#171A20");
    private static readonly IBrush ActiveCardBrush = Brush.Parse("#1D263E");
    private static readonly IBrush SelectedCardBrush = Brush.Parse("#202B48");
    private static readonly IBrush WaitingBorderBrush = Brush.Parse("#2A303A");
    private static readonly IBrush SelectedBorderBrush = Brush.Parse("#6B8BFF");

    public StageRowViewModel(RelayStageDefinition stage)
    {
        Number = stage.Number;
        Name = stage.Name;
        Tier = stage.Tier;
        Status = "Waiting";
    }

    public int Number { get; }
    public string Name { get; }
    public string Tier { get; }
    public string Ordinal => Number.ToString("00");
    public string StatusLabel => Status == "Done" ? "Complete" : Status;
    public string MetricLabel => CostLabel == "No cost yet" ? DurationLabel : $"{DurationLabel}  {CostLabel}";
    public IBrush AccentBrush => Status switch
    {
        "Done" => SuccessBrush,
        "Running" => RunningBrush,
        "Flagged" => FlaggedBrush,
        _ => MutedBrush
    };
    public IBrush CardBackgroundBrush => IsSelected ? SelectedCardBrush : Status == "Running" ? ActiveCardBrush : WaitingCardBrush;
    public IBrush BorderBrush => IsSelected ? SelectedBorderBrush : Status == "Running" ? RunningBrush : WaitingBorderBrush;

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

    public void ApplyMetric(StageRunMetric metric)
    {
        DurationLabel = metric.DurationLabel;
        CostLabel = metric.CostLabel;
        ModelLabel = metric.Model;
        if (Status == "Waiting")
        {
            Status = "Done";
        }
    }

    public void ClearMetric()
    {
        DurationLabel = "No run yet";
        CostLabel = "No cost yet";
        ModelLabel = string.Empty;
    }
}
