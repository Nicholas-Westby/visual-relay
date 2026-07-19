using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls.Buttons;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;

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
            viewModel.StartBackgroundInspections();
            viewModel.StartElapsedTimer();
            viewModel.StartObsidianBridge();

            // RestartBetweenTasks resume: if a fresh handoff sidecar is
            // present, reopen its recorded root path and auto-continue
            // the drain. Stale sidecars are discarded loudly.
            TryAutoResumeFromHandoff(viewModel, window);

            // Localhost HTTP control surface so an operator can drive the app
            // from curl exactly as if clicking its buttons (loopback-only;
            // honors each command's enabled state). When VR_CONTROL_PORT was
            // explicitly set, a bind conflict throws (fail-fast: the control
            // API is load-bearing). Otherwise a bind conflict is surfaced as
            // a persistent banner in the main window.
            var options = ControlServerOptions.FromEnvironment(new ProcessEnvironmentAccessor());
            _controlServer = new ControlServer(new ControlApi(viewModel, window), options);
            try
            {
                _controlServer.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vr-control: fatal — {ex.Message}");
                Environment.Exit(1);
            }
            viewModel.ControlApiUnavailableBanner = !_controlServer.IsAvailable
                ? $"Control API unavailable — port {options.Port} in use by another process"
                : null;
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

    private static void TryAutoResumeFromHandoff(MainWindowViewModel viewModel, Window window)
    {
        // Read the handoff from the current working directory first — the
        // relauncher spawns the new process with WorkingDirectory set to the
        // handoff root, so the sidecar lives at CWD/.relay/restart-handoff.json.
        // Fall back to viewModel.RootPath (e.g. when the app was restarted
        // manually and CWD might differ).
        var handoff = RestartHandoff.Read(Environment.CurrentDirectory)
            ?? RestartHandoff.Read(viewModel.RootPath);
        if (handoff is null) return;

        // Use the handoff's recorded RootPath as the authority for all
        // subsequent operations — it's the repo the run was working on.
        var targetRoot = handoff.RootPath;

        // User pause always wins over auto-continue.
        if (viewModel.PauseRequested) return;

        var now = DateTimeOffset.UtcNow;
        if (RestartHandoff.IsStale(handoff, now))
        {
            DrainSummaryLog.Write(targetRoot, handoff.DrainId,
                "restart-resume", "stale-handoff",
                $"age={now - handoff.Timestamp:g} root-exists={Directory.Exists(handoff.RootPath)}");
            RestartHandoff.Delete(Environment.CurrentDirectory);
            RestartHandoff.Delete(targetRoot);
            return;
        }

        DrainSummaryLog.Write(targetRoot, handoff.DrainId,
            "restart-resume", "pending",
            $"pending={handoff.PendingCount} commit={handoff.CommitSha}");

        // Consume the handoff early — the sidecar must not be left behind
        // if the auto-drain never starts (e.g. empty queue).
        RestartHandoff.MarkConsumed(Environment.CurrentDirectory);
        RestartHandoff.MarkConsumed(targetRoot);

        // Reopen the recorded root path so we work on the right repo.
        viewModel.RootPath = targetRoot;
        _ = viewModel.LoadInitialAsync();

        viewModel.SelectedRunAllMode = RunAllMode.RestartBetweenTasks;

        // Auto-continue the drain on the Dispatcher so UI is fully up first.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _ = viewModel.DrainQueueCommand.ExecuteAsync(null),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Creates the confirm button used in the shared confirmation dialog.
    /// Exposed as internal static so tests can assert its structural
    /// properties without spinning up a full headless window.
    /// </summary>
    internal static CommonButton CreateConfirmButton(string confirmLabel)
    {
        return new CommonButton
        {
            Content = confirmLabel,
            Appearance = ButtonAppearance.Primary,
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
                            new CommonButton
                            {
                                Content = "Cancel",
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
        var cancelBtn = (CommonButton)buttons.Children[0];
        var confirmBtn = (CommonButton)buttons.Children[1];

        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        confirmBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
