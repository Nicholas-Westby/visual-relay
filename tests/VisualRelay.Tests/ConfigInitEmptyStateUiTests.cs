using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

public sealed class ConfigInitEmptyStateUiTests
{
    [AvaloniaFact]
    public async Task InitEmptyState_TypingAndClickingThroughControls_WritesConfigAndFlipsVisibility()
    {
        // ── Arrange: config-less repo with one task ──
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        // ── Show the real window ──
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // QueuePanel is a nested UserControl (its own name scope) embedded without an
        // x:Name in the window, so resolve it from the visual tree and look its named
        // controls up inside its own scope. window.FindControl cannot reach across into
        // a child control's name scope.
        var queuePanel = window.GetVisualDescendants().OfType<QueuePanel>().Single();

        // ── Assert: empty-state Border visible, task ListBox hidden ──
        var initBorder = queuePanel.FindControl<Border>("InitEmptyState");
        var taskList = queuePanel.FindControl<ListBox>("TaskQueueList");
        Assert.NotNull(initBorder);
        Assert.NotNull(taskList);
        Assert.True(initBorder.IsVisible);
        Assert.False(taskList.IsVisible);

        // ── Act: type into the command TextBox via real keyboard input ──
        var textBox = queuePanel.FindControl<TextBox>("InitTestCommandBox");
        Assert.NotNull(textBox);
        textBox.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("dotnet test");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("dotnet test", viewModel.InitTestCommandInput);

        // ── Act: click the Create config button via real mouse input ──
        var button = queuePanel.FindControl<Button>("CreateConfigButton");
        Assert.NotNull(button);
        var buttonCenter = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
        var clickPoint = button.TranslatePoint(buttonCenter, window) ?? buttonCenter;
        window.MouseDown(clickPoint, MouseButton.Left);
        window.MouseUp(clickPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // ── Wait for the async command to settle ──
        await WaitHelpers.WaitUntilWithDispatcherAsync(() => !viewModel.NeedsInitialization);

        // ── Assert: config file exists and parses ──
        Dispatcher.UIThread.RunJobs();
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
        var configText = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"));
        Assert.Contains("dotnet test", configText);

        // ── Assert: visibility flips on the live control tree ──
        Assert.False(viewModel.NeedsInitialization);
        Assert.False(initBorder.IsVisible);
        Assert.True(taskList.IsVisible);
    }

    // WaitUntilWithDispatcherAsync is provided by WaitHelpers.
}
