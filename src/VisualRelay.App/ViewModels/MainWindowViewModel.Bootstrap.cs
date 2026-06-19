using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Init;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    private bool CanBootstrapProject() => !IsBusy && Directory.Exists(RootPath);

    // Makes an empty/greenfield folder runnable in one action: git init + a HEAD
    // commit when missing, a runnable .relay/config.json (a placeholder test command
    // when no toolchain exists yet), and the pre-commit authority hook. The placeholder
    // is upgraded to the real test command automatically once the project gains a
    // toolchain (see EnsureRunnableAsync → ProjectBootstrapper.TryUpgrade...).
    [RelayCommand(CanExecute = nameof(CanBootstrapProject))]
    private async Task BootstrapProjectAsync()
    {
        try
        {
            var result = await ProjectBootstrapper.BootstrapAsync(RootPath);
            var gitNote = result.GitInitialized ? "initialized git repo; " : string.Empty;
            StatusText = result.HookWarning
                ?? (result.UsedPlaceholderTestCommand
                    ? $"Project bootstrapped — {gitNote}placeholder test command set. Add a task that "
                      + "scaffolds the project; the real test command is adopted automatically once a toolchain appears."
                    : $"Project bootstrapped — {gitNote}testCmd: {result.TestCommand}.");
        }
        catch (Exception ex)
        {
            StatusText = $"Bootstrap failed: {ex.Message}";
            return;
        }

        await RefreshAsync();
    }
}
