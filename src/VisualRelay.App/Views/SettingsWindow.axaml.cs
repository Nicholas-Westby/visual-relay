using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VisualRelay.App.Views;

/// <summary>
/// Modal dialog host for the settings panel. Replaces the old Settings flyout,
/// whose FlyoutPresenter added a second scrollbar on top of the panel's own and
/// clipped the content (e.g. "Live Tiers") at its MaxHeight. The window owns the
/// single scroll region and is sized so everything fits at the default size
/// without scrolling.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
