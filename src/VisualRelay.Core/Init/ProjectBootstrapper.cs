using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Core.Init;

/// <summary>Where the written test command came from.</summary>
public enum TestCommandSource
{
    /// <summary>A built-in candidate from the marker-file table, which passed its check.</summary>
    Detected,

    /// <summary>A model proposed it after reading the repository, and it passed the same check.</summary>
    Proposed,

    /// <summary>Nothing passed, so the no-op placeholder was written.</summary>
    Placeholder,
}

/// <summary>Outcome of bootstrapping a target folder for Visual Relay.</summary>
public sealed record ProjectBootstrapResult(
    bool GitInitialized,
    bool HookInstalled,
    string? HookWarning,
    bool UsedPlaceholderTestCommand,
    string TestCommand,
    string ConfigPath,
    SetupCheckDiagnostic? SetupCheck = null,
    TestLayoutDetection? TestLayout = null,
    string? FormatNote = null,
    string? TasksDirNote = null,
    IReadOnlyList<string>? OtherTestCommands = null)
{
    /// <summary>
    /// Why bootstrap refused to run at all, or null when it ran. A refusal writes
    /// nothing: no config, no git repository, no hook. Set on a Windows host with no
    /// usable WSL distro, where a checked test command would be checked through
    /// cmd.exe, on the host, unsandboxed, and the pipeline could never run it.
    /// </summary>
    public string? Refusal { get; init; }

    /// <summary>Where <see cref="TestCommand"/> came from, so the status can say so.</summary>
    public TestCommandSource TestCommandSource { get; init; } = TestCommandSource.Placeholder;

    /// <summary>A refusal, carrying the gate's message and nothing else.</summary>
    /// <param name="refusal">Why bootstrap will not run here.</param>
    /// <returns>A result that wrote nothing.</returns>
    public static ProjectBootstrapResult Refused(string refusal) =>
        new(false, false, null, false, string.Empty, string.Empty) { Refusal = refusal };
}

/// <summary>
/// Asks for a test command when none of bootstrap's own candidates passed. Injected so
/// bootstrap stays testable and so a machine with no provider key simply passes null
/// and keeps today's placeholder.
/// </summary>
/// <param name="attempts">What was tried, and how it failed.</param>
/// <param name="cancellationToken">Cancellation.</param>
/// <returns>The proposed command, or null.</returns>
public delegate Task<string?> ProposeTestCommand(
    IReadOnlyList<CommandAttempt> attempts, CancellationToken cancellationToken);

/// <summary>
/// One-shot "make this folder runnable by Visual Relay" routine. Detects (or
/// placeholders) a test command, writes <c>.relay/config.json</c>, initializes a
/// git repository with a HEAD commit when missing, and installs the pre-commit
/// authority hook. Greenfield-safe: an empty folder becomes runnable, and the
/// placeholder test command is later upgraded to the real one once the project's
/// toolchain exists (see <see cref="TryUpgradePlaceholderTestCommandAsync"/>).
/// </summary>
public static partial class ProjectBootstrapper
{
    /// <summary>
    /// Trivially-green test command written when no toolchain can be detected yet.
    /// Exits 0 under both <c>/bin/sh -lc</c> (the comment is ignored) and direct
    /// exec (<c>true</c> ignores its arguments), so an empty repo's baseline is
    /// green and the first task can scaffold the real project.
    /// </summary>
    public const string PlaceholderTestCommand =
        "true # visual-relay placeholder test command — auto-managed; do not edit";

    /// <summary>
    /// Whether <paramref name="testCommand"/> is the no-op placeholder — a gate that
    /// exits 0 having run nothing, so a green Verify proves nothing about the change.
    /// </summary>
    /// <param name="testCommand">The configured test command.</param>
    /// <returns>True when it is the placeholder.</returns>
    public static bool IsPlaceholder(string? testCommand) =>
        string.Equals(testCommand, PlaceholderTestCommand, StringComparison.Ordinal);

    // The upgrade re-validates the detected command against a freshly-scaffolded
    // project; a first compile (cargo/go/cmake) can be slow, so allow generous time.
    private static readonly TimeSpan UpgradeValidationTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Validation timeout for the manual "Create config" GUI path (2 min).
    /// A first-ever run may cold-compile the whole package (measured ~2 min
    /// for a small SwiftPM package); warm runs are typically 2–3 s. This
    /// value matches <see cref="UpgradeValidationTimeout"/> today but is a
    /// deliberately separate knob for the manual GUI path.
    /// </summary>
    public static readonly TimeSpan CreateConfigValidationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Default validation timeout during init (60 s). Real suites can take ~30 s;
    /// this gives headroom without being unbounded. Once a config exists the
    /// task pipeline uses testTimeoutMs, so this only affects the create-config path.
    /// </summary>
    private static readonly TimeSpan InitValidationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The runner every candidate is smoke-validated with: the SAME shell the pipeline
    /// later runs the command through (<c>/bin/sh -c</c>, on Windows inside the WSL
    /// distro the workspace lives in), so a command with <c>&amp;&amp;</c>, a pipe, a
    /// glob or an env-var prefix is judged as it will actually behave. Argv-splitting
    /// it instead handed the operators to the first program as arguments and rejected
    /// commands that run perfectly well. On a Windows host with no distro there is
    /// nowhere to run it, and <see cref="RefusalFor"/> stops the caller first.
    /// </summary>
    /// <param name="timeout">Time box for the smoke run.</param>
    /// <param name="host">Where the shell runs; null uses this machine.</param>
    /// <returns>The validation runner.</returns>
    public static ITestRunner CreateValidationRunner(TimeSpan timeout, SandboxHost? host = null) =>
        new ShellTestRunner(timeout, loginShell: false, host: host);

    /// <summary>
    /// The runner a PROPOSAL is checked with: the sandbox wrapper the pipeline will run
    /// the command under anyway, which is the right place for a command written by a
    /// model that has been reading an unfamiliar repository. Built-in candidates keep
    /// the bare shell they have always used.
    /// </summary>
    /// <param name="proposal">The proposed command.</param>
    /// <param name="host">Where the sandbox launches from.</param>
    /// <returns>The runner to check it with.</returns>
    internal static ITestRunner CreateProposalRunner(string proposal, SandboxHost host) =>
        new SandboxedTestRunner(
            new ShellTestRunner(ProposalValidationTimeout, loginShell: false, host: host),
            RelayConfigLoader.Defaults(proposal));

    /// <summary>A proposal may build a project it has never built; two minutes matches the manual path.</summary>
    private static readonly TimeSpan ProposalValidationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether this host cannot check a test command where the pipeline would run it.
    /// On Windows every launch goes through the WSL distro; with none resolved, a check
    /// would run on the Windows host, unsandboxed, and the command it accepted could
    /// never run. Bootstrap then writes nothing at all.
    /// <para>
    /// Callers PASS the host; the parameter defaults to the LOCAL host rather than this
    /// machine's, so a test that says nothing about where it runs behaves the same on
    /// every platform instead of being refused only on Windows. Every production caller
    /// passes the resolved host, which <c>BootstrapHostGuardTests</c> pins.
    /// </para>
    /// </summary>
    /// <param name="host">Where the sandbox launches from.</param>
    /// <returns>The gate's refusal message, or null when bootstrap may proceed.</returns>
    internal static string? RefusalFor(SandboxHost host) =>
        host is { IsWindows: true, Wsl: null }
            ? WslGate.Decide(WslContextResolver.UnusableProbe).Message
            : null;

    /// <summary>
    /// Makes <paramref name="rootPath"/> runnable by Visual Relay. Idempotent and
    /// safe on an established repo (never injects a commit when HEAD already exists).
    /// Refuses, writing nothing, where the checked command could not be checked where
    /// the pipeline runs it.
    /// </summary>
    public static async Task<ProjectBootstrapResult> BootstrapAsync(
        string rootPath,
        IGitInvoker? gitInvoker = null,
        ITestRunner? validationRunner = null,
        int? validationTimeoutMs = null,
        SandboxHost? host = null,
        ProposeTestCommand? proposeCommand = null,
        Func<string, ITestRunner>? proposalRunner = null,
        CancellationToken cancellationToken = default)
    {
        var gi = gitInvoker ?? throw new InvalidOperationException("GitInvoker is required but was not provided — callers must inject a real or simulated invoker");
        var sandboxHost = host ?? SandboxHost.Local;
        if (RefusalFor(sandboxHost) is { } refusal)
            return ProjectBootstrapResult.Refused(refusal);

        var timeout = validationTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : InitValidationTimeout;

        // 1. Read the tracked files first: their counts rank the test command candidates, and
        //    step 2c records the test layout they imply.
        var layout = await TestLayoutDetector.DetectAsync(rootPath, gi, cancellationToken);

        // 1a. Resolve a test command: detect + smoke-validate, else a green placeholder
        //     so an empty/greenfield folder is runnable and the first task can scaffold.
        var (command, usedPlaceholder, setupCheck, otherCommands, source) = await ResolveTestCommandAsync(
            rootPath, layout, validationRunner, timeout, sandboxHost, proposeCommand, proposalRunner, cancellationToken);

        // 2. Write .relay/config.json (also writes .relay/.gitignore; detects guard/format).
        var configPath = RelayConfigWriter.Write(rootPath, command);

        // 2a. A formatter the clean checkout does not already satisfy would reformat the
        //     whole project in every task's commit, so it is left out when its check fails.
        var formatNote = await FormatBaselineCheck.ApplyAsync(
            rootPath, validationRunner ?? CreateValidationRunner(timeout, sandboxHost), cancellationToken);

        // 2b. A license audit that fails on visible unlicensed files gets a hidden tasks directory.
        var tasksDirNote = LicenseAuditTasksDir.Apply(rootPath);

        // 2c. Record the author-test defaults the tracked files imply, so the operator can
        //     see (and edit) why Stage 5 will gate their test files the way it does.
        RelayConfigWriter.UpsertAuthorTests(rootPath, layout);

        // 3. Ensure a git repository with a HEAD commit (worktrees + commit stage need it).
        //    A brand-new repo gets an EMPTY initial commit — never the config above.
        var gitInitialized = await GitBootstrapper.EnsureRepositoryAsync(rootPath, gi, cancellationToken);

        // 4. Install the pre-commit authority hook now that a real repo exists.
        var hook = await HookInstaller.InstallAsync(rootPath, cancellationToken, gi);

        // The config is left uncommitted on purpose: bootstrap is a setup step, not
        // an author, and whether Visual Relay's config belongs in the repo's history
        // is the operator's call.
        return new ProjectBootstrapResult(
            gitInitialized, hook.Installed, hook.Warning, usedPlaceholder, command, configPath,
            setupCheck, layout, formatNote, tasksDirNote, otherCommands)
        { TestCommandSource = source };
    }

    /// <summary>
    /// When the config's test command is still the placeholder and the project has
    /// since gained a recognizable toolchain (e.g. a scaffold task added Cargo.toml),
    /// detect + validate the real test command and adopt it, preserving all other
    /// config keys, and refresh the test-layout detection behind it (bootstrap's
    /// detection ran against an empty tree and found nothing; that snapshot must
    /// not survive the project's first real scaffold untouched). Returns true when
    /// an upgrade was applied. No-op (returns false) when the command is not the
    /// placeholder or no toolchain is detectable yet.
    /// </summary>
    public static async Task<bool> TryUpgradePlaceholderTestCommandAsync(
        string rootPath,
        IGitInvoker gitInvoker,
        ITestRunner? validationRunner = null,
        SandboxHost? host = null,
        ProposeTestCommand? proposeCommand = null,
        Func<string, ITestRunner>? proposalRunner = null,
        CancellationToken cancellationToken = default)
    {
        var sandboxHost = host ?? SandboxHost.Local;
        if (RefusalFor(sandboxHost) is not null)
            return false;

        var loaded = await RelayConfigLoader.TryLoadAsync(rootPath, cancellationToken);
        if (loaded.Status != RelayConfigStatus.Loaded
            || !string.Equals(loaded.Config.TestCommand, PlaceholderTestCommand, StringComparison.Ordinal))
        {
            return false;
        }

        var layout = await TestLayoutDetector.DetectAsync(rootPath, gitInvoker, cancellationToken);
        var (command, usedPlaceholder, _, _, _) = await ResolveTestCommandAsync(
            rootPath, layout, validationRunner, UpgradeValidationTimeout, sandboxHost, proposeCommand, proposalRunner, cancellationToken);
        if (usedPlaceholder)
        {
            return false; // still no validatable toolchain — leave the placeholder in place
        }

        RelayConfigWriter.UpsertResolvedToolchain(rootPath, command);
        RelayConfigWriter.UpsertAuthorTests(rootPath, layout);

        return true;
    }

}
