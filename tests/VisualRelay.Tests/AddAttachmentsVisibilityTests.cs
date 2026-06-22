using Avalonia.Threading;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// End-to-end visibility regressions for adding/removing attachments: the edited
/// task must stay selected across the refresh (so the file appears immediately),
/// and a chosen-but-unusable pick must surface a reason instead of a silent no-op.
/// </summary>
[Collection("Headless")]
public sealed class AddAttachmentsVisibilityTests
{
    /// <summary>
    /// Fake picker that returns a scripted <see cref="FilePickResult"/> so tests
    /// can simulate a cancel (0 chosen), a chosen-but-unusable pick (chosen &gt; 0,
    /// 0 resolved paths), or a successful pick (resolved paths).
    /// </summary>
    private sealed class FakeFilePicker(FilePickResult result) : IFilePicker
    {
        public Task<FilePickResult> PickFilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    /// <summary>
    /// Primary visibility regression: adding a file to a task that is NOT the
    /// alphabetically-first task must leave that task selected and surface the
    /// new file in the Attachments tab immediately. Before the fix the refresh
    /// snapped selection back to the first task, so the file looked lost.
    /// </summary>
    [AvaloniaFact]
    public async Task AddAttachments_KeepsEditedTaskSelected_AndShowsNewFile()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Two tasks; "zeta" sorts after "alpha", so it is NOT auto-selected.
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("zeta", "# Zeta\n");

        var sourcePath = Path.Combine(Path.GetTempPath(), $"vr-attach-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(sourcePath, "screenshot bytes");
        var fileName = Path.GetFileName(sourcePath);

        try
        {
            var viewModel = new MainWindowViewModel { RootPath = repo.Root };
            viewModel.UseFilePicker(new FakeFilePicker(new FilePickResult(1, [sourcePath])));
            await viewModel.LoadInitialAsync();

            // Select the non-first task, then add an attachment to it.
            viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "zeta");
            Dispatcher.UIThread.RunJobs();

            await viewModel.AddAttachmentsCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            // (a) Selection must still be the task we edited.
            Assert.NotNull(viewModel.SelectedTask);
            Assert.Equal("zeta", viewModel.SelectedTask.Id);

            // (b) The new file must be visible in the Attachments tab.
            Assert.Contains(
                viewModel.Attachments,
                a => a.Path.EndsWith(fileName, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    /// <summary>
    /// Removing an attachment must keep the user on the same task rather than
    /// snapping selection back to the alphabetically-first task.
    /// </summary>
    [AvaloniaFact]
    public async Task RemoveAttachment_KeepsEditedTaskSelected()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteNestedTask(
            "zeta", "# Zeta\n",
            ("keep.txt", "keep"),
            ("drop.txt", "drop"));

        // Skip the confirm prompt so removal proceeds headlessly.
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            ShowConfirmationAsync = (_, _, _) => Task.FromResult(true)
        };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "zeta");
        Dispatcher.UIThread.RunJobs();

        var dropPath = Path.Combine(repo.Root, "llm-tasks", "zeta", "drop.txt");
        await viewModel.RemoveAttachmentCommand.ExecuteAsync(dropPath);
        Dispatcher.UIThread.RunJobs();

        // Selection stays on the edited task.
        Assert.NotNull(viewModel.SelectedTask);
        Assert.Equal("zeta", viewModel.SelectedTask.Id);

        // The removed file is gone; the kept file remains.
        Assert.DoesNotContain(
            viewModel.Attachments,
            a => a.Path.EndsWith("drop.txt", StringComparison.Ordinal));
        Assert.Contains(
            viewModel.Attachments,
            a => a.Path.EndsWith("keep.txt", StringComparison.Ordinal));
    }

    /// <summary>
    /// Secondary hardening: a pick that chose an item but resolved no local path
    /// must surface a clear reason in StatusText (never a silent no-op), while a
    /// cancel (0 chosen) leaves StatusText untouched.
    /// </summary>
    [AvaloniaFact]
    public async Task AddAttachments_ChosenButUnusable_SetsStatusText_CancelLeavesItUntouched()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");

        // ── Cancel (0 chosen) leaves StatusText untouched ──
        var cancelVm = new MainWindowViewModel { RootPath = repo.Root };
        cancelVm.UseFilePicker(new FakeFilePicker(new FilePickResult(0, [])));
        await cancelVm.LoadInitialAsync();
        cancelVm.SelectedTask = cancelVm.Tasks.Single(t => t.Id == "alpha");
        Dispatcher.UIThread.RunJobs();

        var statusBeforeCancel = cancelVm.StatusText;
        await cancelVm.AddAttachmentsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(statusBeforeCancel, cancelVm.StatusText);

        // ── Chosen-but-unusable (1 chosen, 0 resolved) sets a clear reason ──
        var unusableVm = new MainWindowViewModel { RootPath = repo.Root };
        unusableVm.UseFilePicker(new FakeFilePicker(new FilePickResult(1, [])));
        await unusableVm.LoadInitialAsync();
        unusableVm.SelectedTask = unusableVm.Tasks.Single(t => t.Id == "alpha");
        Dispatcher.UIThread.RunJobs();

        await unusableVm.AddAttachmentsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("local file path", unusableVm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// After a successful add, StatusText must reflect the current queue status
    /// (not a stale prior message or an empty string).
    /// </summary>
    [AvaloniaFact]
    public async Task AddAttachments_Success_SetsQueueStatusText()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("zeta", "# Zeta\n");

        var sourcePath = Path.Combine(Path.GetTempPath(), $"vr-status-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(sourcePath, "bytes");

        try
        {
            var viewModel = new MainWindowViewModel { RootPath = repo.Root };
            viewModel.UseFilePicker(new FakeFilePicker(new FilePickResult(1, [sourcePath])));
            await viewModel.LoadInitialAsync();

            viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "zeta");
            Dispatcher.UIThread.RunJobs();

            // Overwrite StatusText with stale content to confirm it gets replaced.
            viewModel.StatusText = "stale message from a prior operation";

            await viewModel.AddAttachmentsCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            // After a successful add the queue status (e.g. "2 pending") must
            // replace the stale message — not a static string, just not stale.
            Assert.NotEqual("stale message from a prior operation", viewModel.StatusText);
            Assert.Contains("pending", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    /// <summary>
    /// After a successful remove, StatusText must reflect the current queue status.
    /// </summary>
    [AvaloniaFact]
    public async Task RemoveAttachment_Success_SetsQueueStatusText()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteNestedTask(
            "zeta", "# Zeta\n",
            ("drop.txt", "drop"));

        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            ShowConfirmationAsync = (_, _, _) => Task.FromResult(true)
        };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "zeta");
        Dispatcher.UIThread.RunJobs();

        viewModel.StatusText = "stale message from a prior operation";

        var dropPath = Path.Combine(repo.Root, "llm-tasks", "zeta", "drop.txt");
        await viewModel.RemoveAttachmentCommand.ExecuteAsync(dropPath);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual("stale message from a prior operation", viewModel.StatusText);
        Assert.Contains("pending", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The destructive attachment-removal confirmation keeps a "Delete" confirm
    /// label (the shared dialog must not reuse a misleading non-destructive one).
    /// </summary>
    [AvaloniaFact]
    public async Task RemoveAttachment_UsesDeleteConfirmLabel()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("zeta", "# Zeta\n", ("drop.txt", "drop"));

        string? capturedConfirmLabel = null;
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            ShowConfirmationAsync = (_, _, confirmLabel) =>
            {
                capturedConfirmLabel = confirmLabel;
                return Task.FromResult(true);
            }
        };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "zeta");
        Dispatcher.UIThread.RunJobs();

        var dropPath = Path.Combine(repo.Root, "llm-tasks", "zeta", "drop.txt");
        await viewModel.RemoveAttachmentCommand.ExecuteAsync(dropPath);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Delete", capturedConfirmLabel);
    }

    /// <summary>
    /// When a multi-select yields some resolvable paths and some non-local paths,
    /// the resolvable files attach and StatusText surfaces a "skipped" note.
    /// </summary>
    [AvaloniaFact]
    public async Task AddAttachments_PartialDrop_ReportsSkippedItems()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("zeta", "# Zeta\n");

        var sourcePath = Path.Combine(Path.GetTempPath(), $"vr-partial-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(sourcePath, "content");

        try
        {
            // 2 chosen, 1 resolved path → 1 skipped.
            var viewModel = new MainWindowViewModel { RootPath = repo.Root };
            viewModel.UseFilePicker(new FakeFilePicker(new FilePickResult(2, [sourcePath])));
            await viewModel.LoadInitialAsync();

            viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "zeta");
            Dispatcher.UIThread.RunJobs();

            await viewModel.AddAttachmentsCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            // The file that resolved must be attached.
            var fileName = Path.GetFileName(sourcePath);
            Assert.Contains(
                viewModel.Attachments,
                a => a.Path.EndsWith(fileName, StringComparison.Ordinal));

            // And a skipped-items note must appear in StatusText.
            Assert.Contains("skipped", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
