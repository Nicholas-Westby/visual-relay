using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// Two reloads of the task list in flight at once. Each used to clear the list before
/// awaiting the listing, so on the one UI thread both clears ran first and both fills
/// after, leaving every task listed twice until the next reload. Seen on Windows as a
/// created task found twice in the list, 1 full run in 20.
/// </summary>
[Collection("Headless")]
public sealed class TaskListReloadRaceTests
{
    /// <summary>
    /// A refresh started while another is still running takes the mid-run path, which
    /// reloads without waiting, so the two overlap exactly as two callers do in the app.
    /// </summary>
    [AvaloniaFact]
    public async Task TwoOverlappingReloads_ListEachTaskOnce()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        var first = viewModel.RefreshCommand.ExecuteAsync(null);
        var second = viewModel.RefreshCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second);

        Assert.Equal(["alpha", "beta"], viewModel.Tasks.Select(row => row.Id).Order(StringComparer.Ordinal));
    }
}
