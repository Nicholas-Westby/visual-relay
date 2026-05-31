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
    public IBrush AccentBrush => Status switch
    {
        "Done" => SuccessBrush,
        "Running" => RunningBrush,
        "Flagged" => FlaggedBrush,
        _ => MutedBrush
    };
    public IBrush CardBackgroundBrush => Status == "Running" ? ActiveCardBrush : WaitingCardBrush;

    private string _status = "Waiting";
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
            }
        }
    }
}
