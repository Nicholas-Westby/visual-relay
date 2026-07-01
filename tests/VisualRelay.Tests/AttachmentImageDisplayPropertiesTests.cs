using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Property/styling assertions for the inline Image element rendered for
/// image attachments in the Attachments tab.  Verifies MaxWidth constraint,
/// Stretch, and StretchDirection as specified by the display-images-inline
/// feature.
/// </summary>
[Collection("Headless")]
public sealed class AttachmentImageDisplayPropertiesTests
{
    // Minimal 1×1 red PNG (valid, decodable).
    private static readonly byte[] MinimalPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>
    /// The Image element must have a MaxWidth constraint set — this
    /// implements the 50–75% panel-width sizing requirement.
    /// </summary>
    [AvaloniaFact]
    public async Task ImageAttachment_ImageHasMaxWidthConstraint()
    {
        // ── Arrange: task with a .png attachment ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        repo.WriteNestedTask("img-task", "# Image Task\n");
        File.WriteAllBytes(
            Path.Combine(repo.Root, "llm-tasks", "img-task", "screenshot.png"),
            MinimalPngBytes);

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "img-task");
        viewModel.SelectedTabIndex = 2; // Attachments tab
        Dispatcher.UIThread.RunJobs();

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 900,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // ── Act: find the Image element ──
        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        Image? foundImage = null;
        foreach (var sv in taskDetailPanel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var img = sv.GetVisualDescendants()
                .OfType<Image>()
                .FirstOrDefault();
            if (img is not null)
            {
                foundImage = img;
                break;
            }
        }

        // ── Assert: Image exists and has a MaxWidth set ──
        Assert.NotNull(foundImage);
        // MaxWidth should not be default (double.PositiveInfinity) — it must be
        // constrained to a fraction of the parent width.
        Assert.True(
            foundImage!.MaxWidth < double.PositiveInfinity,
            $"Image MaxWidth is {foundImage.MaxWidth}, " +
            "must be a finite value (bound to a fraction of panel width).");
    }

    /// <summary>
    /// The Image element must have Stretch set to Uniform so the aspect
    /// ratio is preserved when scaling.
    /// </summary>
    [AvaloniaFact]
    public async Task ImageAttachment_ImageHasUniformStretch()
    {
        // ── Arrange ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        repo.WriteNestedTask("img-task", "# Image Task\n");
        File.WriteAllBytes(
            Path.Combine(repo.Root, "llm-tasks", "img-task", "screenshot.png"),
            MinimalPngBytes);

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "img-task");
        viewModel.SelectedTabIndex = 2;
        Dispatcher.UIThread.RunJobs();

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 900,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        Image? foundImage = null;
        foreach (var sv in taskDetailPanel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var img = sv.GetVisualDescendants()
                .OfType<Image>()
                .FirstOrDefault();
            if (img is not null)
            {
                foundImage = img;
                break;
            }
        }

        Assert.NotNull(foundImage);
        Assert.Equal(Avalonia.Media.Stretch.Uniform, foundImage!.Stretch);
    }

    /// <summary>
    /// The Image element must have StretchDirection set to DownOnly so
    /// small images are not upscaled beyond their native resolution.
    /// </summary>
    [AvaloniaFact]
    public async Task ImageAttachment_ImageHasDownOnlyStretchDirection()
    {
        // ── Arrange ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        repo.WriteNestedTask("img-task", "# Image Task\n");
        File.WriteAllBytes(
            Path.Combine(repo.Root, "llm-tasks", "img-task", "screenshot.png"),
            MinimalPngBytes);

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "img-task");
        viewModel.SelectedTabIndex = 2;
        Dispatcher.UIThread.RunJobs();

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 900,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        Image? foundImage = null;
        foreach (var sv in taskDetailPanel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var img = sv.GetVisualDescendants()
                .OfType<Image>()
                .FirstOrDefault();
            if (img is not null)
            {
                foundImage = img;
                break;
            }
        }

        Assert.NotNull(foundImage);
        Assert.Equal(Avalonia.Media.StretchDirection.DownOnly, foundImage!.StretchDirection);
    }
}
