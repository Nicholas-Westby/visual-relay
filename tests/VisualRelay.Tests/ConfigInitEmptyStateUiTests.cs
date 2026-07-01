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

[Collection("Headless")]
public sealed class ConfigInitEmptyStateUiTests
{
    [AvaloniaFact]
    public async Task InitEmptyState_TypingAndClickingThroughControls_WritesConfigAndFlipsVisibility()
    {
        // ── Arrange: config-less repo with one task ──
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
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

        // ── Act: click the Create config button and await the command's
        // in-flight task deterministically (no wall-clock poll). ──
        var button = queuePanel.FindControl<Button>("CreateConfigButton");
        Assert.NotNull(button);
        var buttonCenter = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
        var clickPoint = button.TranslatePoint(buttonCenter, window) ?? buttonCenter;
        window.MouseDown(clickPoint, MouseButton.Left);
        window.MouseUp(clickPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var createTask = viewModel.CreateConfigCommand.ExecutionTask;
        Assert.NotNull(createTask);
        await createTask;
        Dispatcher.UIThread.RunJobs();

        // ── Assert: config file exists and parses ──
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
        var configText = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"));
        Assert.Contains("dotnet test", configText);

        // ── Assert: visibility flips on the live control tree ──
        Assert.False(viewModel.NeedsInitialization);
        Assert.False(initBorder.IsVisible);
        Assert.True(taskList.IsVisible);
    }

    /// <summary>
    /// Mirrors the real startup order: the VM is constructed with an empty
    /// RootPath, the window (and thus the bootstrap button binding) is created,
    /// and only later is RootPath set to an existing directory.  This reproduces
    /// the stale-CanExecute bug — <c>CanBootstrapProject()</c> evaluates
    /// <c>Directory.Exists("")</c> once and is never re-queried because neither
    /// <c>_rootPath</c> nor <c>_isBusy</c> carries
    /// <c>[NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))]</c>.
    /// Fails today (button stuck disabled); passes once the notify attributes
    /// are added.
    /// </summary>
    [AvaloniaFact]
    public async Task BootstrapButton_EnablesAfterRootPathSet_WhenMirroringRealInitOrder()
    {
        // ── Arrange: config-less repo with one task ──
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");

        // CRUX: construct VM WITHOUT RootPath (default empty).  In the real app
        // the window + bindings exist before LoadInitialAsync or BrowseAsync
        // sets RootPath, so the bootstrap button’s CanExecute is evaluated when
        // Directory.Exists("") = false and never re-queried.
        var viewModel = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });

        // ── Show the real window (button binds while RootPath is empty) ──
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // ── Now set RootPath to an existing directory (mirrors live BrowseAsync
        //    or LoadInitialAsync discovering an existing folder) ──
        viewModel.RootPath = repo.Root;
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization,
            "NeedsInitialization must be true so the init card (and its buttons) are realized");
        Dispatcher.UIThread.RunJobs();

        // Resolve QueuePanel from the visual tree
        var queuePanel = window.GetVisualDescendants().OfType<QueuePanel>().Single();

        // ── Sanity: empty-state Border visible ──
        var initBorder = queuePanel.FindControl<Border>("InitEmptyState");
        Assert.NotNull(initBorder);
        Assert.True(initBorder.IsVisible);

        // ── Find the "Set up empty project" button by content (no x:Name) ──
        var allButtons = queuePanel.GetVisualDescendants().OfType<Button>().ToList();
        var bootstrapButton = allButtons.FirstOrDefault(
            b => b.Content?.ToString() == "Set up empty project");
        Assert.NotNull(bootstrapButton);

        // ── Assert: button should be enabled now that RootPath points at an
        //    existing directory.  Fails today because CanExecute was frozen at
        //    startup (Directory.Exists("") = false) and never re-evaluated. ──
        Assert.True(bootstrapButton.IsEffectivelyEnabled,
            "Bootstrap button should be enabled after RootPath is set to an existing directory; "
            + "it was frozen disabled because CanExecute was evaluated once at startup "
            + "(when RootPath was empty) and never re-queried.");
    }
}
