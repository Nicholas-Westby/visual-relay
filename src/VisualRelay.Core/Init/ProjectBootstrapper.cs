using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Core.Init;

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
    string? FormatNote = null);

/// <summary>
/// One-shot "make this folder runnable by Visual Relay" routine. Detects (or
/// placeholders) a test command, writes <c>.relay/config.json</c>, initializes a
/// git repository with a HEAD commit when missing, and installs the pre-commit
/// authority hook. Greenfield-safe: an empty folder becomes runnable, and the
/// placeholder test command is later upgraded to the real one once the project's
/// toolchain exists (see <see cref="TryUpgradePlaceholderTestCommandAsync"/>).
/// </summary>
public static class ProjectBootstrapper
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
    /// later runs the command through (<c>/bin/sh -c</c>; on Windows inside the WSL
    /// distro the workspace lives in, or <c>cmd.exe /c</c> where no distro is
    /// resolved), so a command with <c>&amp;&amp;</c>, a pipe, a glob or an env-var
    /// prefix is judged as it will actually behave. Argv-splitting it instead handed
    /// the operators to the first program as arguments and rejected commands that run
    /// perfectly well.
    /// </summary>
    /// <param name="timeout">Time box for the smoke run.</param>
    /// <returns>The validation runner.</returns>
    public static ITestRunner CreateValidationRunner(TimeSpan timeout) =>
        new ShellTestRunner(timeout, loginShell: false);

    /// <summary>
    /// Makes <paramref name="rootPath"/> runnable by Visual Relay. Idempotent and
    /// safe on an established repo (never injects a commit when HEAD already exists).
    /// </summary>
    public static async Task<ProjectBootstrapResult> BootstrapAsync(
        string rootPath,
        IGitInvoker? gitInvoker = null,
        ITestRunner? validationRunner = null,
        int? validationTimeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var gi = gitInvoker ?? throw new InvalidOperationException("GitInvoker is required but was not provided — callers must inject a real or simulated invoker");
        var timeout = validationTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : InitValidationTimeout;

        // 1. Resolve a test command: detect + smoke-validate, else a green placeholder
        //    so an empty/greenfield folder is runnable and the first task can scaffold.
        var (command, usedPlaceholder, setupCheck) = await ResolveTestCommandAsync(
            rootPath, validationRunner, timeout, cancellationToken);

        // 2. Write .relay/config.json (also writes .relay/.gitignore; detects guard/format).
        var configPath = RelayConfigWriter.Write(rootPath, command);

        // 2a. A formatter the clean checkout does not already satisfy would reformat the
        //     whole project in every task's commit, so it is left out when its check fails.
        var formatNote = await FormatBaselineCheck.ApplyAsync(
            rootPath, validationRunner ?? CreateValidationRunner(timeout), cancellationToken);

        // 2b. Detect the test layout from the tracked files and record the
        //     author-test defaults it implies, so the operator can see (and edit)
        //     why Stage 5 will gate their test files the way it does.
        var layout = await TestLayoutDetector.DetectAsync(rootPath, gi, cancellationToken);
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
            setupCheck, layout, formatNote);
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
        CancellationToken cancellationToken = default)
    {
        var loaded = await RelayConfigLoader.TryLoadAsync(rootPath, cancellationToken);
        if (loaded.Status != RelayConfigStatus.Loaded
            || !string.Equals(loaded.Config.TestCommand, PlaceholderTestCommand, StringComparison.Ordinal))
        {
            return false;
        }

        var (command, usedPlaceholder, _) = await ResolveTestCommandAsync(
            rootPath, validationRunner, UpgradeValidationTimeout, cancellationToken);
        if (usedPlaceholder)
        {
            return false; // still no validatable toolchain — leave the placeholder in place
        }

        RelayConfigWriter.UpsertResolvedToolchain(rootPath, command);

        var layout = await TestLayoutDetector.DetectAsync(rootPath, gitInvoker, cancellationToken);
        RelayConfigWriter.UpsertAuthorTests(rootPath, layout);

        return true;
    }

    // Detect candidates and return the first that smoke-validates; otherwise the
    // placeholder. The runner/timeout are injectable so callers (init vs. upgrade)
    // pick their own timeout and tests pass a fake.
    private static async Task<(string Command, bool UsedPlaceholder, SetupCheckDiagnostic? SetupCheck)> ResolveTestCommandAsync(
        string rootPath, ITestRunner? validationRunner, TimeSpan validationTimeout, CancellationToken cancellationToken)
    {
        var candidates = TestCommandDetector.DetectCandidates(rootPath);
        var timeoutMs = (int)validationTimeout.TotalMilliseconds;
        if (candidates.Count > 0)
        {
            var runner = validationRunner ?? CreateValidationRunner(validationTimeout);
            var validator = new TestCommandValidator(runner);
            var rejections = new List<(string, string, int, bool, string)>();

            foreach (var candidate in candidates)
            {
                var result = await validator.ValidateAsync(rootPath, candidate, cancellationToken);
                if (result.Accepted)
                {
                    return (candidate, false, null);
                }

                rejections.Add((
                    candidate,
                    result.RejectionReason ?? "unknown",
                    result.RunResult.ExitCode,
                    result.RunResult.TimedOut,
                    result.RunResult.Output));
            }

            // All candidates rejected — build a diagnostic from the LAST failure.
            var last = rejections[^1];
            var diag = SetupCheckDiagnostic.FromFailedValidation(
                rootPath, last.Item1, timeoutMs, last.Item3, last.Item4, last.Item5, rejections);
            return (PlaceholderTestCommand, true, diag);
        }

        return (PlaceholderTestCommand, true, null);
    }
}
