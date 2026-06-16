using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelTests
{
    /// <summary>
    /// When the selection-load Task faults (disk I/O error, missing file, etc.),
    /// the VM must surface that fault to <see cref="MainWindowViewModel.StatusText"/>
    /// — the VM's established operation-error channel — instead of silently
    /// swallowing it into a discarded <c>_</c> fire-and-forget Task.
    /// Before the fix this test FAILS because the exception is swallowed and
    /// StatusText never leaves "Idle"; after the fix StatusText carries the
    /// fault message and the test PASSES.
    /// </summary>
    [Fact]
    public async Task SelectingTask_LoadFault_SurfacesErrorInStatusText()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Create two tasks: LoadInitialAsync auto-selects the first (alpha),
        // leaving beta untouched — so selecting beta triggers a fresh load
        // that will fault on the deleted markdown file.
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("broken", "# Broken\n");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Delete the markdown file for the task that has NOT been
        // selected yet (beta), so the next selection load faults.
        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "broken.md");
        File.Delete(markdownPath);

        var statusBefore = viewModel.StatusText;
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "broken");

        // Await the real load Task — deterministic, no wall-clock budget.
        await viewModel.LastSelectionLoad!;

        Assert.NotEqual(statusBefore, viewModel.StatusText);
        Assert.NotEqual("Idle", viewModel.StatusText);
    }
}
