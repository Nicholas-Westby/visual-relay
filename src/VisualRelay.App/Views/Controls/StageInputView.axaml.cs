using System;
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

    public async void CopyPromptSectionBody_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is PromptSection section)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is { } clipboard)
            {
                await clipboard.SetValueAsync(DataFormat.Text, section.Body);
            }
        }
    }
}
