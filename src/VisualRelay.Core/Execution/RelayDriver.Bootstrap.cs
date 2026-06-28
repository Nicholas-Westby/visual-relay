using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    // ── Bootstrap detection ────────────────────────────────────────────

    private static readonly IReadOnlyList<string> BuiltInBootstrapGlobs =
    [
        "flake.nix", "flake.lock", "*.nix", "Brewfile", "Dockerfile*",
        ".tool-versions", "rust-toolchain*"
    ];

    private const string BuiltInBootstrapCommand = "nix develop --command true";

    /// <summary>
    /// Simple filename-based glob matching. A glob starting with <c>*</c> matches
    /// any filename whose suffix matches the rest; a glob ending with <c>*</c>
    /// matches any filename whose prefix matches the rest; otherwise exact match.
    /// </summary>
    private static bool MatchesBootstrapGlob(string relativePath, string glob)
    {
        var fileName = Path.GetFileName(relativePath);
        if (glob.StartsWith('*'))
            return fileName.EndsWith(glob[1..], StringComparison.Ordinal);
        if (glob.EndsWith('*'))
            return fileName.StartsWith(glob[..^1], StringComparison.Ordinal);
        return string.Equals(fileName, glob, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns <c>(shouldRun: true, command)</c> when the manifest touches a
    /// bootstrap file and a smoke command is resolved; otherwise
    /// <c>(false, "")</c>.
    /// </summary>
    private static (bool ShouldRun, string Command) ResolveBootstrapCheck(
        RelayConfig config,
        IReadOnlyList<string> manifest)
    {
        var globs = config.BootstrapFiles is { Count: > 0 }
            ? config.BootstrapFiles
            : BuiltInBootstrapGlobs;

        // An explicit empty-string entry disables built-in detection.
        if (globs is [""])
            return (false, string.Empty);

        var matchedAny = manifest.Any(path =>
            globs.Any(glob => MatchesBootstrapGlob(path, glob)));

        if (!matchedAny)
            return (false, string.Empty);

        string? command = config.BootstrapCheckCommand;
        if (command is not null)
            return (true, command);

        // Auto-detect: only nix repos (any .nix file matched) get the built-in.
        var hasNix = manifest.Any(path =>
            Path.GetExtension(path).Equals(".nix", StringComparison.OrdinalIgnoreCase));
        if (hasNix)
            return (true, BuiltInBootstrapCommand);

        // Matched a glob but no recognised toolchain — skip.
        return (false, string.Empty);
    }

    /// <summary>
    /// Runs the optional <see cref="RelayConfig.BuildCommand"/> through the same
    /// <see cref="ITestRunner"/> as the test command so it inherits the same sandbox
    /// (nono-wrapped in production, mocked in tests). The build runs BEFORE the timed
    /// test phase and is bounded by its OWN (generous) budget,
    /// <see cref="RelayConfig.BuildTimeoutMilliseconds"/> — threaded in via the
    /// explicit-hard-cap RunAsync overload — NOT the shorter
    /// <see cref="RelayConfig.TestTimeoutMilliseconds"/>, so the timed test budget then
    /// covers only the suite. 0 = no wall-clock cap. The idle-reap watchdog still
    /// applies (it reaps a finished build whose nono wrapper lingers); builds are
    /// CPU-active so a working build is never false-reaped. Returns a
    /// <see cref="TestRunResult"/> surfacing exit code, output, and timeout.
    /// </summary>
    private async Task<TestRunResult> RunBuildCommandAsync(
        string rootPath, RelayConfig config, CancellationToken ct)
    {
        var buildCap = config.BuildTimeoutMilliseconds <= 0
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMilliseconds(config.BuildTimeoutMilliseconds);
        return await _dependencies.TestRunner.RunAsync(rootPath, config.BuildCommand!, buildCap, ct);
    }

    private async Task<TestRunResult> RunTestCommandWithRetryAsync(
        string rootPath, RelayConfig config, CancellationToken ct,
        int stageNumber, string runId, string taskId)
    {
        // Run the build phase first (once only — retry reuses warm artifacts).
        if (config.BuildCommand is not null)
        {
            var buildResult = await RunBuildCommandAsync(rootPath, config, ct);
            if (buildResult.TimedOut || buildResult.ExitCode != 0)
            {
                await _dependencies.EventSink.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow, "error", "verify_build_failed", runId, rootPath, taskId, stageNumber,
                    Data: new Dictionary<string, string>
                    {
                        ["reason"] = buildResult.TimedOut ? "timeout" : $"exit-code-{buildResult.ExitCode}",
                    }), ct);
                return buildResult;
            }
        }

        var result = await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, ct);
        if (result.TimedOut || result.ExitCode == 0 || !config.RetryFlakyVerify)
            return result;

        // Retry fires — emit warn event
        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "verify_retry", runId, rootPath, taskId, stageNumber,
            Data: new Dictionary<string, string> { ["reason"] = "first-run-nonzero" }), ct);

        var retryResult = await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, ct);

        if (retryResult is { ExitCode: 0, TimedOut: false })
        {
            // Fail→pass flip — emit info event
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "info", "verify_retry_pass", runId, rootPath, taskId, stageNumber,
                Data: new Dictionary<string, string>
                {
                    ["result"] = "pass-on-retry",
                    ["classification"] = "flaky"   // first-run red flipped green on re-run = non-deterministic
                }), ct);
            return retryResult with { Elapsed = result.Elapsed + retryResult.Elapsed };
        }

        return result with { Elapsed = result.Elapsed + retryResult.Elapsed }; // both failed — return original with total elapsed
    }

    /// <summary>
    /// The command the Fix-verify (stage 10) agent is told to run to self-verify. When a
    /// separate <see cref="RelayConfig.BuildCommand"/> is configured the test command is
    /// typically <c>--no-build</c>; running it alone would self-verify the agent's edits
    /// against STALE artifacts (a false green that drives non-convergent loops), so the
    /// build is prepended (<c>buildCmd &amp;&amp; testCmd</c>) to compile the edits first.
    /// With no BuildCommand the behavior is unchanged (the test command builds itself).
    /// Keyed purely on config — no repo/toolchain specifics.
    /// </summary>
    internal static string AgentFixVerifyCommand(RelayConfig config) =>
        string.IsNullOrWhiteSpace(config.BuildCommand)
            ? config.TestCommand
            : $"{config.BuildCommand} && {config.TestCommand}";

    /// <summary>
    /// Multiplies <paramref name="value"/> by <see cref="TurnBoostMultiplier"/> in
    /// <see cref="long"/> and saturates to <see cref="int.MaxValue"/>, so a large
    /// configured turn budget or subagent timeout cannot overflow <see cref="int"/> to a
    /// negative value — which silently defeated the boost (a negative ceiling reads as
    /// "disabled" downstream).
    /// </summary>
    internal static int SaturatingBoost(int value)
    {
        var scaled = (long)value * TurnBoostMultiplier;
        return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
    }
}
