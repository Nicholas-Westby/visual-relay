using Avalonia.Controls;
using Avalonia.Interactivity;
using VisualRelay.App.ViewModels;

namespace VisualRelay.App.Views.Controls;

public partial class TopBar : UserControl
{
    public TopBar()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens settings as a modal dialog (replacing the old flyout). The flyout's
    /// FlyoutPresenter added a second scrollbar over the panel's own and clipped
    /// the content; a window owns a single scroll region and fits everything.
    /// </summary>
    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        // Event handler must be async void; guard so an unhandled exception in
        // the dialog flow can never tear down the process.
        try
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            // Prevent opening a second settings window if one is already open.
            if (vm.IsSettingsOpen)
                return;

            try
            {
                await vm.OpenSettingsAsync();

                var dialog = new SettingsWindow { DataContext = vm };
                if (TopLevel.GetTopLevel(this) is Window owner)
                {
                    await dialog.ShowDialog(owner);
                }
                else
                {
                    // Owner-null only happens in off-desktop environments (e.g. headless
                    // unit tests without a top-level window). Fall back to non-modal.
                    dialog.Show();
                }
            }
            finally
            {
                // Always clear IsSettingsOpen — even if OpenSettingsAsync/ShowDialog throws —
                // otherwise the duplicate-open guard above would block reopening until restart.
                vm.CloseSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Settings dialog failed: {ex}");
        }
    }
}
