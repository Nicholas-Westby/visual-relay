using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // Injectable factory for Create config validation. Null → DirectExecTestRunner
    // with CreateConfigValidationTimeout. Tests inject a fake without real processes.
    public Func<TimeSpan, ITestRunner>? InitValidationRunnerFactory { get; set; }

    // Injectable runner for the baseline guard check in EnsureRunnableAsync. Null →
    // the same sandboxed runner the pipeline uses. Tests inject a fake so the gate is
    // exercised without spawning a real build.
    public Func<RelayConfig, ITestRunner>? BaselineGuardRunnerFactory { get; init; }

    // Injectable resolver for where the sandbox runs (EnsureRunnableAsync). Null →
    // this machine, awaited rather than waited on. Tests inject a host so the Windows
    // arm — whose real resolution is a wsl.exe probe — is gated on any OS.
    public Func<Task<SandboxHost>>? SandboxHostResolver { get; init; }

    // Injectable git identity check for EnsureRunnableAsync: (workspace root, whether git
    // runs inside a WSL distro) to a refusal or null. Null → GitIdentityGate with real git.
    public Func<string, bool, Task<string?>>? GitIdentityCheck { get; init; }

    private bool CanBootstrapProject() => !IsBusy && Directory.Exists(RootPath);

    // Makes an empty/greenfield folder runnable in one action: git init + a HEAD
    // commit when missing, a runnable .relay/config.json (a placeholder test command
    // when no toolchain exists yet), and the pre-commit authority hook. The placeholder
    // is upgraded to the real test command automatically once the project gains a
    // toolchain (see EnsureRunnableAsync → ProjectBootstrapper.TryUpgrade...).
    [RelayCommand(CanExecute = nameof(CanBootstrapProject))]
    private async Task BootstrapProjectAsync()
    {
        string outcome;
        try
        {
            var result = await ProjectBootstrapper.BootstrapAsync(RootPath, new GitInvoker());
            SetupCheck = result.SetupCheck;
            outcome = DescribeBootstrap(result);
        }
        catch (Exception ex)
        {
            SetupCheck = null;
            StatusText = $"Bootstrap failed: {ex.Message}";
            return;
        }

        await RefreshAsync();
        // AFTER the refresh: its idle branch ends by writing the queue count, so a
        // status set before it is never what the operator (or /state) reads.
        StatusText = outcome;
    }

    /// <summary>
    /// What bootstrap leaves the operator to act on. The config note is unconditional:
    /// the file was written into their tree and nobody committed it, and that is as
    /// true when a foreign pre-commit hook took the headline as when it did not.
    /// </summary>
    private static string DescribeBootstrap(ProjectBootstrapResult result)
    {
        var gitNote = result.GitInitialized ? "initialized git repo; " : string.Empty;
        var headline = result.HookWarning
            ?? (result.UsedPlaceholderTestCommand
                ? $"Project bootstrapped — {gitNote}placeholder test command set. Add a task that "
                  + "scaffolds the project; the real test command is adopted automatically once a toolchain appears."
                : $"Project bootstrapped — {gitNote}testCmd: {result.TestCommand}.");
        var notes = string.Concat(new[] { result.FormatNote, result.TasksDirNote }.OfType<string>().Select(note => " " + note));
        return headline + " " + DescribeTestLayout(result.TestLayout) + notes
               + " Config written to .relay/config.json and left uncommitted.";
    }

    /// <summary>
    /// What init read off the tracked files. The languages decide how the
    /// Author-tests stage gates the operator's test files, so the sentence is
    /// written even when nothing was detected.
    /// </summary>
    private static string DescribeTestLayout(TestLayoutDetection? layout)
    {
        if (layout is null || layout.DetectedLanguages.Count == 0)
            return "Detected languages: none.";

        var languages = string.Join(", ", layout.DetectedLanguages);
        return layout.InlineTestExtensions.Count == 0
            ? $"Detected languages: {languages}."
            : $"Detected languages: {languages} (inline tests: {string.Join(", ", layout.InlineTestExtensions)}).";
    }
}
