using Avalonia.Controls;
using Avalonia.Input;
using VisualRelay.App.ViewModels;

namespace VisualRelay.App.Views.Controls;

public partial class TaskDetailPanel : UserControl
{
    public TaskDetailPanel()
    {
        InitializeComponent();
    }

    private void OnAttachmentImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Image image)
            return;

        if (image.DataContext is not AttachmentRowViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(image);
        if (topLevel?.Launcher is { } launcher)
        {
            _ = launcher.LaunchUriAsync(new Uri(vm.Path));
        }
    }
}
