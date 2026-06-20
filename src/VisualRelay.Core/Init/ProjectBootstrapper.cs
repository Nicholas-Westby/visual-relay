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
    string ConfigPath);

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

    // The upgrade re-validates the detected command against a freshly-scaffolded
    // project; a first compile (cargo/go/cmake) can be slow, so allow generous time.
    private static readonly TimeSpan UpgradeValidationTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Makes <paramref name="rootPath"/> runnable by Visual Relay. Idempotent and
    /// safe on an established repo (never injects a commit when HEAD already exists).
    /// </summary>
    public static async Task<ProjectBootstrapResult> BootstrapAsync(
        string rootPath,
        IGitInvoker? gitInvoker = null,
        ITestRunner? validationRunner = null,
        CancellationToken cancellationToken = default)
    {
        var gi = gitInvoker ?? new GitInvoker();

        // 1. Resolve a test command: detect + smoke-validate, else a green placeholder
        //    so an empty/greenfield folder is runnable and the first task can scaffold.
        var (command, usedPlaceholder) = await ResolveTestCommandAsync(
            rootPath, validationRunner, TimeSpan.FromSeconds(5), cancellationToken);

        // 2. Write .relay/config.json (also writes .relay/.gitignore; detects guard/format).
        var configPath = RelayConfigWriter.Write(rootPath, command);

        // 3. Ensure a git repository with a HEAD commit (worktrees + commit stage need it).
        //    A brand-new repo's initial commit includes the .relay config written above.
        var gitInitialized = await GitBootstrapper.EnsureRepositoryAsync(rootPath, gi, cancellationToken);

        // 4. Install the pre-commit authority hook now that a real repo exists.
        var hook = await HookInstaller.InstallAsync(rootPath, cancellationToken, gi);

        return new ProjectBootstrapResult(
            gitInitialized, hook.Installed, hook.Warning, usedPlaceholder, command, configPath);
    }

    /// <summary>
    /// When the config's test command is still the placeholder and the project has
    /// since gained a recognizable toolchain (e.g. a scaffold task added Cargo.toml),
    /// detect + validate the real test command and adopt it, preserving all other
    /// config keys. Returns true when an upgrade was applied. No-op (returns false)
    /// when the command is not the placeholder or no toolchain is detectable yet.
    /// </summary>
    public static async Task<bool> TryUpgradePlaceholderTestCommandAsync(
        string rootPath,
        ITestRunner? validationRunner = null,
        CancellationToken cancellationToken = default)
    {
        var loaded = await RelayConfigLoader.TryLoadAsync(rootPath, cancellationToken);
        if (loaded.Status != RelayConfigStatus.Loaded
            || !string.Equals(loaded.Config.TestCommand, PlaceholderTestCommand, StringComparison.Ordinal))
        {
            return false;
        }

        var (command, usedPlaceholder) = await ResolveTestCommandAsync(
            rootPath, validationRunner, UpgradeValidationTimeout, cancellationToken);
        if (usedPlaceholder)
        {
            return false; // still no validatable toolchain — leave the placeholder in place
        }

        RelayConfigWriter.UpsertResolvedToolchain(rootPath, command);
        return true;
    }

    // Detect candidates and return the first that smoke-validates; otherwise the
    // placeholder. The runner/timeout are injectable so callers (init vs. upgrade)
    // pick their own timeout and tests pass a fake.
    private static async Task<(string Command, bool UsedPlaceholder)> ResolveTestCommandAsync(
        string rootPath, ITestRunner? validationRunner, TimeSpan validationTimeout, CancellationToken cancellationToken)
    {
        var candidates = TestCommandDetector.DetectCandidates(rootPath);
        if (candidates.Count > 0)
        {
            var runner = validationRunner ?? new DirectExecTestRunner(validationTimeout);
            var validator = new TestCommandValidator(runner);
            foreach (var candidate in candidates)
            {
                var result = await validator.ValidateAsync(rootPath, candidate, cancellationToken);
                if (result.Accepted)
                {
                    return (candidate, false);
                }
            }
        }

        return (PlaceholderTestCommand, true);
    }
}
