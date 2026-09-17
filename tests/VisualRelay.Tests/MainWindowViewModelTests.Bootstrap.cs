using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

// Greenfield bootstrap wiring: the GUI can make an empty folder runnable, and the
// run gate adopts the real test command once a placeholder project gains a toolchain.
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task BootstrapProjectCommand_EmptyFolder_MakesItRunnable()
    {
        using var repo = TestRepository.Create(); // empty: no git, no config

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization); // precondition: not runnable yet

        await viewModel.BootstrapProjectCommand.ExecuteAsync(null);

        Assert.True(Directory.Exists(Path.Combine(repo.Root, ".git")));
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, loaded.Status);
        Assert.False(viewModel.NeedsInitialization); // refresh cleared the init banner
    }

    /// <summary>
    /// The note is the ONE thing bootstrap leaves the operator to act on: a config
    /// file written into their tree that no one committed. The command's own refresh
    /// ends by writing the idle queue count, so the note has to outlive it.
    /// </summary>
    [Fact]
    public async Task BootstrapProjectCommand_StatusText_KeepsTheConfigNoteAfterItsRefresh()
    {
        using var repo = TestRepository.Create();
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        await viewModel.BootstrapProjectCommand.ExecuteAsync(null);

        Assert.Contains(ConfigNote, viewModel.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A repository that already has someone else's pre-commit hook still had its
    /// config written, so the warning must not cost the operator that note.
    /// </summary>
    [Fact]
    public async Task BootstrapProjectCommand_WithAForeignHook_ReportsTheWarningAndTheConfigNote()
    {
        using var repo = TestRepository.Create();
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        await viewModel.BootstrapProjectCommand.ExecuteAsync(null); // git init + VR hook
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".git", "hooks", "pre-commit"),
            "#!/bin/sh" + Environment.NewLine + "exit 0" + Environment.NewLine,
            TestContext.Current.CancellationToken);

        await viewModel.BootstrapProjectCommand.ExecuteAsync(null);

        Assert.Contains("not written by Visual Relay", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains(ConfigNote, viewModel.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The detected languages decide how Stage 5 gates the operator's test files,
    /// so bootstrap says what it found even when the answer is nothing.
    /// </summary>
    [Fact]
    public async Task BootstrapProjectCommand_StatusText_NamesTheDetectedLanguages()
    {
        using var repo = TestRepository.Create();
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        await viewModel.BootstrapProjectCommand.ExecuteAsync(null);

        Assert.Contains("Detected languages: none.", viewModel.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bootstrap validates one test command. When the repository holds another toolchain's, the
    /// status names it, so the operator can see a suite Verify will not run.
    /// </summary>
    [Fact]
    public void DescribeBootstrap_NamesTheTestCommandsItDidNotUse()
    {
        var result = new ProjectBootstrapResult(
            GitInitialized: false, HookInstalled: true, HookWarning: null, UsedPlaceholderTestCommand: false,
            TestCommand: "cargo test", ConfigPath: ".relay/config.json",
            OtherTestCommands: ["node --test tests/tooling/*.test.js", "go test ./..."]);

        Assert.Contains(
            "testCmd: cargo test. Also detected, not used: \"node --test tests/tooling/*.test.js\", \"go test ./...\".",
            MainWindowViewModel.DescribeBootstrap(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// Someone else's pre-commit hook does not hide what bootstrap did about the test command.
    /// Measured on the Mac with moment/luxon, which has a husky hook: the status gave only the hook
    /// warning, and the placeholder test command bootstrap had written went unmentioned.
    /// </summary>
    [Fact]
    public void DescribeBootstrap_WithAForeignHook_StillSaysWhichTestCommandItSet()
    {
        var result = new ProjectBootstrapResult(
            GitInitialized: false, HookInstalled: false,
            HookWarning: "A pre-commit hook already exists at /repo/.husky/pre-commit and was not written by Visual Relay.",
            UsedPlaceholderTestCommand: true, TestCommand: ProjectBootstrapper.PlaceholderTestCommand,
            ConfigPath: ".relay/config.json");

        var text = MainWindowViewModel.DescribeBootstrap(result);

        Assert.Contains("placeholder test command set", text, StringComparison.Ordinal);
        Assert.Contains("A pre-commit hook already exists", text, StringComparison.Ordinal);
    }

    private const string ConfigNote = "Config written to .relay/config.json and left uncommitted.";

    private static ProjectBootstrapResult BootstrapSucceeded() =>
        new(GitInitialized: false, HookInstalled: true, HookWarning: null,
            UsedPlaceholderTestCommand: false, TestCommand: "npm test",
            ConfigPath: ".relay/config.json");

    /// <summary>
    /// Bootstrap runs every candidate test command and the formatter check, which on a
    /// large project takes minutes. A script driving the app over the control API read
    /// isBusy=false and the idle queue line the whole time, so it could not tell that
    /// bootstrap was running and run-all was not refused.
    /// </summary>
    [Fact]
    public async Task BootstrapProjectCommand_WhileItRuns_IsBusyAndTheStatusNamesTheStep()
    {
        using var repo = TestRepository.Create();
        var held = new TaskCompletionSource();
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            BootstrapRunner = async _ =>
            {
                await held.Task;
                return BootstrapSucceeded();
            },
        };
        await viewModel.LoadInitialAsync();

        var running = viewModel.BootstrapProjectCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsBusy);
        Assert.Equal("Bootstrapping: checking test commands", viewModel.StatusText);
        Assert.False(viewModel.BootstrapProjectCommand.CanExecute(null));

        held.SetResult();
        await running;

        Assert.False(viewModel.IsBusy);
        Assert.Contains(ConfigNote, viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapProjectCommand_WhenItThrows_EndsNotBusy()
    {
        using var repo = TestRepository.Create();
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            BootstrapRunner = _ => throw new InvalidOperationException("no git"),
        };
        await viewModel.LoadInitialAsync();

        await viewModel.BootstrapProjectCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsBusy);
        Assert.Equal("Bootstrap failed: no git", viewModel.StatusText);
    }

    [Fact]
    public async Task EnsureRunnableAsync_UpgradesPlaceholder_WhenToolchainAppears()
    {
        using var repo = TestRepository.Create();
        // Greenfield: bootstrap to a placeholder, then a "scaffold task" added a toolchain
        // marker. The injected validation runner keeps the upgrade hermetic: no npm needed.
        await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: new GitSimEngine());
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), "{\"scripts\":{\"test\":\"true\"}}");

        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            InitValidationRunnerFactory = _ => new ScriptedTestRunner(new TestRunResult(0, "ok")),
        };
        await viewModel.LoadInitialAsync();
        viewModel.IsHuggingFaceConfigured = true;

        await viewModel.EnsureRunnableAsync(pendingTaskId: null);

        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal("npm test", loaded.Config.TestCommand); // adopted the detected command
        Assert.NotEqual(ProjectBootstrapper.PlaceholderTestCommand, loaded.Config.TestCommand);
    }
}
