using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Regression anchor: in the read-only Markdown tab the task title must
/// render exactly once — as the <c># Title</c> line inside the
/// <see cref="MainWindowViewModel.SelectedTaskMarkdown"/> body.
///
/// Before the fix a bold <see cref="TextBlock"/> bound to
/// <see cref="MainWindowViewModel.SelectedTaskName"/> duplicated the
/// title above the body, so two visible TextBlocks contained the title
/// text.  The fix removes that bold heading; the test asserts the count
/// drops from 2 to 1.
/// </summary>
[Collection("Headless")]
public sealed class TaskDetailMarkdownTitleDeduplicationTests
{
    /// <summary>
    /// In the read-only Markdown tab, exactly one visible
    /// <see cref="TextBlock"/> should contain the task title text.
    /// Before the deduplication fix the bold heading (bound to
    /// <c>SelectedTaskName</c>) plus the body (bound to
    /// <c>SelectedTaskMarkdown</c>, whose first line is the
    /// <c># Title</c>) both contained it, yielding a count of 2.
    /// </summary>
    [AvaloniaFact]
    public async Task ReadOnlyMarkdownTab_RendersTitle_ExactlyOnce()
    {
        // ── Arrange: seed a task whose markdown begins with a # Title ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("feature-x", "# Implement Feature X\n\nThis is the body.\nMore content.");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Select the task and wait for the async markdown/context load.
        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Dispatcher.UIThread.RunJobs();

        // Switch to the Markdown tab (index 0) so IsMarkdownReadOnly is true.
        viewModel.SelectedTabIndex = 0;
        Dispatcher.UIThread.RunJobs();

        // ── Show the window so the visual tree is materialized ──
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 900,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // ── Collect all visible TextBlocks inside the TaskDetailPanel
        //    whose Text contains the title substring ──
        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        var titleTextBlocks = taskDetailPanel.GetVisualDescendants()
            .OfType<TextBlock>()
            // ReSharper disable once MergeIntoPattern — IsVisible + Text null check would need pattern variable rename
            .Where(tb => tb.IsVisible && tb.Text is not null && tb.Text.Contains("Implement Feature X"))
            .ToList();

        // ── Assert: exactly one TextBlock contains the title text ──
        Assert.Single(titleTextBlocks);
    }
}
