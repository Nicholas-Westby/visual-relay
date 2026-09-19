using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Services;
using VisualRelay.Core.Agent;
using VisualRelay.Core.Configuration;
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

    // Injectable bootstrap seam: (root path) → the outcome. Null → ProjectBootstrapper
    // with real git. Tests inject one they can hold open, so what /state reports while
    // bootstrap is still running can be asserted.
    public Func<string, Task<ProjectBootstrapResult>>? BootstrapRunner { get; init; }

    // Injectable proposer seam, so a test scripts what the model would answer. Null →
    // the real agent run.
    internal ProposeTestCommand? TestCommandProposerFor { get; init; }

    /// <summary>
    /// The proposer bootstrap falls back to when none of its own candidates passed: a
    /// small agent run with the normal tool catalog, so it can read the project's CI
    /// configuration and scripts and try its answer before giving it. Null on a machine
    /// with no provider key, where the fallback is skipped rather than started and
    /// failed, and null where the sandbox cannot run, because the agent's commands need it.
    /// </summary>
    /// <param name="host">Where the sandbox launches from.</param>
    /// <returns>The proposer, or null when one cannot be built here.</returns>
    private ProposeTestCommand? BuildTestCommandProposer(SandboxHost host)
    {
        if (TestCommandProposerFor is { } injected)
            return injected;
        if (!IsHuggingFaceConfigured)
            return null;

        var config = RelayConfigLoader.Defaults(ProjectBootstrapper.PlaceholderTestCommand);
        if (SandboxedStage.MissingRequiredTools(config, host: host).Count > 0)
            return null;

        return async (attempts, ct) =>
        {
            var sink = new ObservableRelayEventSink(HandleRelayEvent);
            var runner = SubagentRunnerFactory.Create(config, sink, Env, VerboseSandboxDiagnostics);
            return await TestCommandProposer.RunAsync(RootPath, attempts, config, runner, ct, sink);
        };
    }

    /// <summary>
    /// The same proposer, asked the per-file question. Built from the same conditions,
    /// so a machine that cannot ask for one cannot ask for the other either.
    /// </summary>
    /// <param name="host">Where the sandbox launches from.</param>
    /// <returns>The per-file proposer, or null when one cannot be built here.</returns>
    private ProposePerFileCommand? BuildPerFileProposer(SandboxHost host)
    {
        if (BuildTestCommandProposer(host) is null)
            return null;

        var config = RelayConfigLoader.Defaults(ProjectBootstrapper.PlaceholderTestCommand);
        return async (testCommand, testFiles, rejectedForm, ct) =>
        {
            var sink = new ObservableRelayEventSink(HandleRelayEvent);
            var runner = SubagentRunnerFactory.Create(config, sink, Env, VerboseSandboxDiagnostics);
            return await TestCommandProposer.RunPerFileAsync(
                RootPath, testCommand, testFiles, rejectedForm, config, runner, ct, sink);
        };
    }

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
        // Bootstrap runs every test command candidate and the formatter check, which
        // takes minutes on a large project. Without this a polling script read
        // isBusy=false and the idle queue line the whole time, and run-all was not
        // refused. The existing refusals key on IsBusy, so they now cover bootstrap.
        IsBusy = true;
        StatusText = "Bootstrapping: checking test commands";
        try
        {
            var host = await ResolveSandboxHostAsync();
            var result = BootstrapRunner is { } run
                ? await run(RootPath)
                : await ProjectBootstrapper.BootstrapAsync(
                    RootPath, new GitInvoker(), host: host,
                    proposeCommand: BuildTestCommandProposer(host),
                    proposePerFile: BuildPerFileProposer(host));
            SetupCheck = result.SetupCheck;
            // A refusal wrote nothing, so there is no outcome to describe: the gate's
            // own message is what the operator has to act on.
            outcome = result.Refusal ?? DescribeBootstrap(result);
        }
        catch (Exception ex)
        {
            SetupCheck = null;
            StatusText = $"Bootstrap failed: {ex.Message}";
            return;
        }
        finally
        {
            // Before the refresh below, so it takes its ordinary idle branch and the
            // closing sentence is still written last.
            IsBusy = false;
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
    internal static string DescribeBootstrap(ProjectBootstrapResult result)
    {
        var gitNote = result.GitInitialized ? "initialized git repo; " : string.Empty;
        // A foreign hook's warning follows the test command sentence instead of replacing it: luxon's
        // husky hook once hid that bootstrap had written the placeholder.
        var headline = result.UsedPlaceholderTestCommand
            ? $"Project bootstrapped — {gitNote}{DescribePlaceholder(result)}"
            : $"Project bootstrapped — {gitNote}testCmd: {result.TestCommand}{DescribeSource(result)}.{DescribeOtherTestCommands(result)}";
        var warning = result.HookWarning is { } hookWarning ? " " + hookWarning : string.Empty;
        var notes = string.Concat(new[] { result.FormatNote, result.TasksDirNote }.OfType<string>().Select(note => " " + note));
        return headline + warning + DescribePerFileCommand(result) + " " + DescribeTestLayout(result.TestLayout) + notes
               + " Config written to .relay/config.json and left uncommitted.";
    }

    /// <summary>
    /// What the author-tests gate will run on every task, and whether anything proved
    /// it. The gate ran this command on files a task had just written with nothing ever
    /// having run it first, so a wrong form made the gate fail for the wrong reason.
    /// </summary>
    private static string DescribePerFileCommand(ProjectBootstrapResult result) =>
        result.PerFileCommandSource switch
        {
            PerFileCommandSource.Table => " testFileCmd proven on the repository's own test files.",
            PerFileCommandSource.Proposed => " testFileCmd proposed by the model and proven on the repository's own test files.",
            PerFileCommandSource.Unproven => " testFileCmd written unproven: the repository has no test file to prove it with.",
            _ when result.UsedPlaceholderTestCommand => string.Empty,
            _ => " No per-file test command passed its proof; the author-tests gate will run the whole suite.",
        };

    /// <summary>
    /// A command a model wrote is worth flagging even though it passed the same check
    /// a built-in candidate has to: the operator should look at it once.
    /// </summary>
    private static string DescribeSource(ProjectBootstrapResult result) =>
        result.TestCommandSource == TestCommandSource.Proposed
            ? " (proposed by the model and checked; review it in .relay/config.json)"
            : string.Empty;

    /// <summary>
    /// The placeholder headline. The scaffolding advice is for a GREENFIELD folder;
    /// on a repository that HAS source files it was misleading, because the honest
    /// answer is that nothing tried passed and here is what was tried. At most three
    /// attempts are named; the log has the rest.
    /// </summary>
    private static string DescribePlaceholder(ProjectBootstrapResult result)
    {
        if (result.SetupCheck?.Rejections is not { Count: > 0 } rejections)
        {
            return "placeholder test command set. Add a task that scaffolds the project; "
                + "the real test command is adopted automatically once a toolchain appears.";
        }

        var named = string.Join("; ", rejections.Take(3).Select(r => $"{r.Candidate}: {FirstLine(r.Reason)}"));
        return $"No test command passed the check ({named}). Set testCmd in .relay/config.json.";
    }

    private static string FirstLine(string text) =>
        text.Split('\n')[0].Trim();

    // Another toolchain's test command is a suite Verify will not run; naming it lets the operator
    // notice when bootstrap validated the wrong one.
    private static string DescribeOtherTestCommands(ProjectBootstrapResult result) =>
        result.OtherTestCommands is { Count: > 0 } others
            ? $" Also detected, not used: {string.Join(", ", others.Select(command => $"\"{command}\""))}."
            : string.Empty;

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
