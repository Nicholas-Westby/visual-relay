using CommunityToolkit.Mvvm.Input;

namespace VisualRelay.App.ViewModels;

public sealed class AttachmentRowViewModel : ViewModelBase
{
    public AttachmentRowViewModel(string path, IRelayCommand revealCommand, IRelayCommand removeCommand)
    {
        Path = path;
        RevealCommand = revealCommand;
        RemoveCommand = removeCommand;
    }

    public string Path { get; }
    public IRelayCommand RevealCommand { get; }
    public IRelayCommand RemoveCommand { get; }
}
