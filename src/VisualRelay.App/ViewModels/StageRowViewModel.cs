using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public sealed class StageRowViewModel : ViewModelBase
{
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

    private string _status = "Waiting";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}
