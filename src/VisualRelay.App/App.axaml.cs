using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.App;

public partial class App : Application
{
    private ControlServer? _controlServer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // The macOS application menu (the bold first menu, next to the Apple logo)
        // derives its title from Application.Name; left unset it defaults to
        // "Avalonia Application". Set it so the unbundled `dotnet run` / bare
        // published launch shows the product name. (The .app bundle independently
        // sets CFBundleName via tools/VisualRelay.Packaging.)
        Name = "Visual Relay";
        System.Diagnostics.Debug.WriteLine($"Visual Relay version: {Domain.VersionHelper.ReadInformationalVersion()}");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var viewModel = new MainWindowViewModel();
            viewModel.UseFolderPicker(new AvaloniaFolderPicker(window));
            viewModel.UseFilePicker(new AvaloniaFilePicker(window));
            viewModel.ShowConfirmationAsync = (title, message, confirmLabel) =>
                ShowConfirmationAsync(window, title, message, confirmLabel);
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            _ = viewModel.LoadInitialAsync();
            viewModel.StartBackendMonitoring();
            viewModel.StartElapsedTimer();
            viewModel.StartObsidianBridge();

            // Localhost HTTP control surface so an operator can drive the app
            // from curl exactly as if clicking its buttons (loopback-only;
            // honors each command's enabled state). A startup failure (e.g. port
            // in use) is swallowed inside ControlServer.Start — never blocks the
            // app. Stop it on exit so the socket is released.
            var options = ControlServerOptions.FromEnvironment(new ProcessEnvironmentAccessor());
            _controlServer = new ControlServer(new ControlApi(viewModel, window), options);
            _controlServer.Start();
            desktop.Exit += (_, _) =>
            {
                _controlServer?.Stop();
                // Delete any un-reverted rewrite-undo snapshots so they never leak.
                viewModel.DiscardPendingRewriteUndos();
            };
            desktop.ShutdownRequested += (_, _) => _controlServer?.Stop();
        }

        // Best-effort: show the brand icon in the macOS Dock. AppKit is live by
        // now, so this covers the dev `dotnet run` path and the bare published
        // exec (neither runs inside a .app bundle); harmless inside the bundle.
        // No-op off macOS; never throws or blocks startup.
        MacDockIcon.TrySet();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates the confirm button used in the shared confirmation dialog.
    /// Exposed as internal static so tests can assert its structural
    /// properties without spinning up a full headless window.
    /// </summary>
    internal static Button CreateConfirmButton(string confirmLabel)
    {
        return new Button
        {
            Content = confirmLabel,
            // Grow to fit a longer label (e.g. "Rewrite and Replace")
            // rather than clipping at a fixed width.
            MinWidth = 80,
            Padding = new Thickness(12, 0),
            Height = 32,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            // The Fluent Button ControlTheme does NOT set
            // VerticalContentAlignment, so it falls to Top.  The Cancel
            // button inherits theme ButtonPadding (with vertical inset)
            // that masks the Top default; our Padding=(12,0) zeroes the
            // vertical padding and exposes it.  Center explicitly.
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
    }

    private static async Task<bool> ShowConfirmationAsync(Window owner, string title, string message, string confirmLabel)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            CanResize = false,
            Background = Brush.Parse("#1A1E26"),
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Margin = new Thickness(20),
                RowSpacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = Brush.Parse("#DCE2EA"),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        [Grid.RowProperty] = 0
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        [Grid.RowProperty] = 1,
                        Children =
                        {
                            new Button
                            {
                                Content = "Cancel",
                                Width = 80,
                                Height = 32
                            },
                            CreateConfirmButton(confirmLabel)
                        }
                    }
                }
            }
        };

        // Wire up button clicks.
        var grid = (Grid)dialog.Content;
        var buttons = (StackPanel)grid.Children[1];
        var cancelBtn = (Button)buttons.Children[0];
        var confirmBtn = (Button)buttons.Children[1];

        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        confirmBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
