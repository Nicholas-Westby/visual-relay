using CommunityToolkit.Mvvm.Input;

namespace VisualRelay.App.ViewModels;

public sealed class AttachmentRowViewModel(string path, IRelayCommand revealCommand, IRelayCommand removeCommand)
    : ViewModelBase
{
    public string Path { get; } = path;
    public IRelayCommand RevealCommand { get; } = revealCommand;
    public IRelayCommand RemoveCommand { get; } = removeCommand;
}
