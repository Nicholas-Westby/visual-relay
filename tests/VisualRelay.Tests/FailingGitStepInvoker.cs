using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Forwards every git call to an inner invoker, except a call whose argument vector
/// starts with <paramref name="step"/>: that one answers <paramref name="exitCode"/> and
/// <paramref name="output"/> without running, the way a real git refuses one step of a
/// sequence (commit-tree with no identity, bundle create on a full disk).
/// </summary>
/// <param name="inner">The invoker every other call is forwarded to.</param>
/// <param name="step">The leading arguments of the call that fails, e.g. <c>["bundle", "create"]</c>.</param>
/// <param name="exitCode">The exit code the failing call reports.</param>
/// <param name="output">What git says when it refuses.</param>
internal sealed class FailingGitStepInvoker(IGitInvoker inner, string[] step, int exitCode, string output) : IGitInvoker
{
    public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string rootPath, IEnumerable<string> arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default, Action<string>? onActivity = null)
    {
        var args = arguments.ToArray();
        return args.Take(step.Length).SequenceEqual(step, StringComparer.Ordinal)
            ? Task.FromResult((exitCode, output, false))
            : inner.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
    }
}
