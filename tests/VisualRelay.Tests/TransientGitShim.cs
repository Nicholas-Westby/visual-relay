using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Implements <see cref="IGitInvoker"/> for tests that need to simulate
/// transient git failures within the <see cref="GitCommitter"/> retry loop.
/// Intercepts git calls whose argument list contains a configured substring
/// and returns synthetic failures for a specified count before delegating to
/// an inner invoker (a GitSim, in the migrated pilot).
/// </summary>
internal sealed class TransientGitShim(IGitInvoker inner) : IGitInvoker
{
    private readonly Dictionary<string, int> _failureCounts = new();
    private readonly Dictionary<string, int> _consumed = new();
    private int _exitCode = 128;
    private string _stderr = "fatal: transient error";

    /// <summary>
    /// Configure the next <paramref name="failureCount"/> git invocations whose
    /// arguments contain <paramref name="argumentSubstring"/> to return a
    /// synthetic failure instead of calling real git.
    /// </summary>
    public void FailNext(string argumentSubstring, int failureCount, int exitCode = 128, string stderr = "fatal: transient error")
    {
        _failureCounts[argumentSubstring] = failureCount;
        _exitCode = exitCode;
        _stderr = stderr;
    }

    public int Consumed(string argumentSubstring) =>
        _consumed.GetValueOrDefault(argumentSubstring);

    public async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string rootPath, IEnumerable<string> arguments, CancellationToken ct,
        TimeSpan? timeout, IReadOnlyDictionary<string, string>? environment,
        CancellationToken killToken = default, Action<string>? onActivity = null)
    {
        var argsList = arguments.ToList();
        var argsStr = string.Join(' ', argsList);
        foreach (var kvp in _failureCounts)
        {
            if (argsStr.Contains(kvp.Key, StringComparison.Ordinal) && kvp.Value > 0)
            {
                _failureCounts[kvp.Key] = kvp.Value - 1;
                _consumed[kvp.Key] = _consumed.GetValueOrDefault(kvp.Key) + 1;
                return (_exitCode, _stderr, false);
            }
        }

        // Synthetic failures spent: delegate to the inner invoker (GitSim).
        return await inner.RunAsync(rootPath, argsList, ct, timeout, environment, killToken, onActivity);
    }
}
