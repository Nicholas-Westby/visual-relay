using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Runs the repository's guard command once against the tree as it stands BEFORE any
/// task touches it.
/// <para>
/// <c>baselineVerify</c> already diffs guard output against a stashed baseline, but only
/// after a task's own gate has gone red, and it never stops the queue. A guard that
/// cannot pass on this machine at all — a toolchain the outer sandbox blocks, a missing
/// build tool — therefore reached every task in the drain as a per-task red, one
/// escalation ladder each. Answering the question once, up front, turns that into a
/// single refusal an operator can act on.
/// </para>
/// </summary>
public static class BaselineGuardGate
{
    /// <summary>How much of the guard's own output the refusal message carries.</summary>
    private const int OutputTailChars = 200;

    /// <summary>
    /// Checks the guard against the untouched tree.
    /// </summary>
    /// <param name="rootPath">The repository root.</param>
    /// <param name="config">The loaded relay configuration.</param>
    /// <param name="testRunner">Runs the guard command the way the pipeline would.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>
    /// <c>null</c> when no guard is configured or it passes; otherwise the
    /// operator-facing reason to refuse to run tasks.
    /// </returns>
    public static async Task<string?> CheckAsync(
        string rootPath,
        RelayConfig config,
        ITestRunner testRunner,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.GuardCommand)) return null;

        var result = await testRunner.RunAsync(rootPath, config.GuardCommand, cancellationToken);
        return result.ExitCode == 0 ? null : BuildMessage(config.GuardCommand, result);
    }

    /// <summary>Names the guard, its verdict, and the tail of what it printed.</summary>
    /// <param name="guardCommand">The configured guard command.</param>
    /// <param name="result">What running it produced.</param>
    /// <returns>The refusal message.</returns>
    private static string BuildMessage(string guardCommand, TestRunResult result)
    {
        var verdict = result.TimedOut ? "timed out" : $"fails (exit {result.ExitCode})";
        var message =
            $"Guard command '{guardCommand}' {verdict} on the untouched tree, so every task "
            + "would be flagged for it. Fix the environment, then run again.";
        var tail = Tail(result.Output);
        return tail.Length == 0 ? message : $"{message} Guard said: {tail}";
    }

    private static string Tail(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;
        var flat = string.Join(' ', output.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= OutputTailChars ? flat : "…" + flat[^OutputTailChars..];
    }
}
