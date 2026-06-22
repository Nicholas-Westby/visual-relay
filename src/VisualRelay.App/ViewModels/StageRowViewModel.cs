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

    // The duration lives on the status row, freeing the fixed-width metrics line
    // to carry only cost + turns + test (which the 165 px card can show in full).
    // Running shows the live, per-second elapsed so the card visibly ticks; a
    // finished stage reads "Completed in 17s" (just "Complete" when no duration
    // was recorded), and other statuses pass through unchanged.
    public string StatusLabel => Status switch
    {
        "Running" => string.IsNullOrEmpty(ElapsedLabel) ? "Running" : $"Running {ElapsedLabel}",
        "Done" => HasRecordedDuration ? $"Completed in {DurationLabel}" : "Complete",
        _ => Status
    };

    // True once a real duration has been recorded (not the "No run yet"
    // placeholder / empty), so StatusLabel knows whether to show it.
    private bool HasRecordedDuration =>
        !string.IsNullOrEmpty(DurationLabel) && DurationLabel != "No run yet";

    // The metrics line: cost + turns + test, joined by two spaces with empties
    // dropped. The duration token now lives on the status row, so a long
    // completed card no longer overflows the fixed 165 px width.
    public string MetricLabel => string.Join("  ", MetricTokens());

    private IEnumerable<string> MetricTokens()
    {
        if (CostLabel != "No cost yet")
            yield return CostLabel;
        if (!string.IsNullOrEmpty(TurnsLabel))
            yield return TurnsLabel;
        if (!string.IsNullOrEmpty(TestDurationLabel))
            yield return $"test {TestDurationLabel}";
    }
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
    private DateTimeOffset? _runningSince;
    private string _elapsedLabel = string.Empty;
    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                // Leaving Running (e.g. stage_done/flag) ends live ticking; drop
                // the elapsed so the status row reverts from the live tick to the
                // final "Completed in {DurationLabel}".
                if (value != "Running")
                {
                    _runningSince = null;
                    ElapsedLabel = string.Empty;
                }
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(AccentBrush));
                OnPropertyChanged(nameof(CardBackgroundBrush));
                OnPropertyChanged(nameof(BorderBrush));
                OnPropertyChanged(nameof(CardBorderThickness));
                OnPropertyChanged(nameof(CardShadow));
            }
        }
    }

    /// <summary>
    /// Live, per-second elapsed shown on the active stage card (e.g. "2m 25s").
    /// Empty unless the stage is Running and a tick has populated it. Formatted
    /// by <see cref="ElapsedFormatter"/> — the same formatter the running-task
    /// elapsed label uses — so the "2m 25s" style stays consistent.
    /// </summary>
    public string ElapsedLabel
    {
        get => _elapsedLabel;
        private set
        {
            if (SetProperty(ref _elapsedLabel, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    /// <summary>
    /// Marks this stage Running and captures the moment it started, so the
    /// 1-second timer can tick its elapsed label. Mirrors the running-task
    /// elapsed approach (a captured UtcNow start + periodic recompute).
    /// </summary>
    public void MarkRunning(DateTimeOffset startedAt)
    {
        _runningSince = startedAt;
        ElapsedLabel = string.Empty;
        Status = "Running";
    }

    /// <summary>
    /// Per-tick refresh invoked by the 1-second DispatcherTimer. No-op unless the
    /// stage is Running with a captured start; never touches the final
    /// DurationLabel recorded on stage_done.
    /// </summary>
    public void RefreshElapsed(DateTimeOffset now)
    {
        if (Status == "Running" && _runningSince is { } since)
        {
            ElapsedLabel = ElapsedFormatter.Label(now - since);
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
                // The duration now renders on the status row ("Completed in 17s"),
                // not in the metrics line.
                OnPropertyChanged(nameof(StatusLabel));
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

    private string _testDurationLabel = string.Empty;
    public string TestDurationLabel
    {
        get => _testDurationLabel;
        set
        {
            if (SetProperty(ref _testDurationLabel, value))
            {
                OnPropertyChanged(nameof(MetricLabel));
            }
        }
    }

    public void SetTestDurationSeconds(double? seconds)
    {
        TestDurationLabel = seconds.HasValue ? FormatDuration(seconds.Value) : string.Empty;
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60)
            return $"{Math.Max(0, seconds):0}s";
        var minutes = Math.Floor(seconds / 60);
        var remainder = seconds % 60;
        return $"{minutes:0}m {remainder:00}s";
    }

    public string? ReportPath { get; private set; }
    private string? TraceDirectory { get; set; }

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
        TestDurationLabel = string.Empty;
        ReportPath = null;
        TraceDirectory = null;
        _runningSince = null;
        ElapsedLabel = string.Empty;
    }
}
