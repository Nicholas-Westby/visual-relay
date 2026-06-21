using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using VisualRelay.App.Services;

namespace VisualRelay.App.Views.Controls;

public partial class StageInputView : UserControl
{
    public StageInputView()
    {
        InitializeComponent();
    }

    private async void CopyPromptSectionBody_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Event handler must be async void; guard so an unhandled exception in
        // the clipboard flow can never tear down the process.
        try
        {
            if (sender is Button { DataContext: PromptSection section }
                && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetValueAsync(DataFormat.Text, section.Body);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Copy prompt section failed: {ex}");
        }
    }
}
