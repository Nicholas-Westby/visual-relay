using System.Windows.Input;

namespace VisualRelay.App.ViewModels;

public sealed class AttachmentRowViewModel : ViewModelBase
{
    public AttachmentRowViewModel(string path, ICommand revealCommand, ICommand removeCommand)
    {
        Path = path;
        RevealCommand = revealCommand;
        RemoveCommand = removeCommand;
    }

    public string Path { get; }
    public ICommand RevealCommand { get; }
    public ICommand RemoveCommand { get; }
}
