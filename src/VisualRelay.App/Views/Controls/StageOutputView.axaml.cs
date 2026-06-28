using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using VisualRelay.App.Services;

namespace VisualRelay.App.Views.Controls;

public partial class StageOutputView : UserControl
{
    public StageOutputView()
    {
        InitializeComponent();
    }

    private async void CopyOutputFieldValue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Event handler must be async void; guard so an unhandled exception in
        // the clipboard flow can never tear down the process.
        try
        {
            if (sender is Button { DataContext: OutputField field }
                && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetValueAsync(DataFormat.Text, field.Value);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Copy output field failed: {ex}");
        }
    }
}
