using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.Tests;

public sealed class ConfigInitEmptyStateUiTests
{
    [Fact]
    public async Task InitEmptyState_TypingAndClickingThroughControls_WritesConfigAndFlipsVisibility()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(async () =>
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

            // ── Assert: empty-state Border visible, task ListBox hidden ──
            var initBorder = window.FindControl<Border>("InitEmptyState");
            var taskList = window.FindControl<ListBox>("TaskQueueList");
            Assert.NotNull(initBorder);
            Assert.NotNull(taskList);
            Assert.True(initBorder.IsVisible);
            Assert.False(taskList.IsVisible);

            // ── Act: type into the command TextBox via real keyboard input ──
            var textBox = window.FindControl<TextBox>("InitTestCommandBox");
            Assert.NotNull(textBox);
            textBox.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("dotnet test");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("dotnet test", viewModel.InitTestCommandInput);

            // ── Act: click the Create config button via real mouse input ──
            var button = window.FindControl<Button>("CreateConfigButton");
            Assert.NotNull(button);
            var buttonCenter = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
            var clickPoint = button.TranslatePoint(buttonCenter, window) ?? buttonCenter;
            window.MouseDown(clickPoint, MouseButton.Left);
            window.MouseUp(clickPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // ── Wait for the async command to settle ──
            await WaitUntilAsync(() => !viewModel.NeedsInitialization);

            // ── Assert: config file exists and parses ──
            Dispatcher.UIThread.RunJobs();
            Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
            var configText = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"));
            Assert.Contains("dotnet test", configText);

            // ── Assert: visibility flips on the live control tree ──
            Assert.False(viewModel.NeedsInitialization);
            Assert.False(initBorder.IsVisible);
            Assert.True(taskList.IsVisible);
        }, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(condition());
    }
}
