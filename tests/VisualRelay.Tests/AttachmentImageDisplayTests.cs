using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Integration tests that verify inline image display in the Attachments tab.
///
/// When an attachment is an image (.png, .jpg, .jpeg, .gif, .bmp, .webp),
/// an <see cref="Image"/> element must render above the Reveal/Remove buttons
/// and file-path text. Non-image attachments must NOT render any Image element.
/// Both Reveal and Remove buttons must still be present for image attachments.
/// </summary>
[Collection("Headless")]
public sealed class AttachmentImageDisplayTests
{
    // Minimal 1×1 red PNG (valid, decodable).
    private static readonly byte[] MinimalPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>
    /// When the selected task has a .png sibling, the Attachments tab must render
    /// an Image element with a non-null Source. This is the primary functional
    /// assertion for inline image display.
    /// </summary>
    [AvaloniaFact]
    public async Task ImageAttachment_RendersImageElement()
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

        // ── Act: find the TaskDetailPanel, then search for Image elements ──
        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        Image? foundImage = null;
        foreach (var sv in taskDetailPanel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            // The Image element is inside the ItemsControl's DataTemplate.
            var img = sv.GetVisualDescendants()
                .OfType<Image>()
                .FirstOrDefault();
            if (img is not null)
            {
                foundImage = img;
                break;
            }
        }

        // ── Assert: an Image element must exist with a non-null Source ──
        Assert.NotNull(foundImage);
        Assert.NotNull(foundImage!.Source);
    }

    /// <summary>
    /// A non-image attachment (.txt) must NOT render any Image element inside
    /// the item template. This ensures the Image is only shown for recognized
    /// image extensions.
    /// </summary>
    [AvaloniaFact]
    public async Task NonImageAttachment_DoesNotRenderImageElement()
    {
        // ── Arrange: task with a .txt attachment ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        repo.WriteNestedTask("txt-task", "# Text Task\n", ("notes.txt", "hello"));

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "txt-task");
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

        // ── Act: find the TaskDetailPanel, then search for visible Image elements ──
        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        Image? foundImage = null;
        foreach (var sv in taskDetailPanel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var img = sv.GetVisualDescendants()
                .OfType<Image>()
                .FirstOrDefault(i => i.IsVisible);
            if (img is not null)
            {
                foundImage = img;
                break;
            }
        }

        // ── Assert: no visible Image element for a non-image attachment ──
        Assert.Null(foundImage);
    }

    /// <summary>
    /// Image attachments must still expose Reveal and Remove buttons.
    /// The inline image preview is additive — it does not replace the
    /// existing action buttons.
    /// </summary>
    [AvaloniaFact]
    public async Task ImageAttachment_HasRevealAndRemoveButtons()
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

        // ── Act: locate Reveal and Remove buttons in the Attachments scroll area ──
        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        Button? revealButton = null;
        Button? removeButton = null;
        foreach (var sv in taskDetailPanel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var buttons = sv.GetVisualDescendants().OfType<Button>().ToList();
            revealButton = buttons.FirstOrDefault(b => b.Content?.ToString() == "Reveal");
            removeButton = buttons.FirstOrDefault(b => b.Content?.ToString() == "Remove");
            if (revealButton is not null && removeButton is not null)
                break;
        }

        // ── Assert: both buttons must be present ──
        Assert.NotNull(revealButton);
        Assert.NotNull(removeButton);
    }


}
