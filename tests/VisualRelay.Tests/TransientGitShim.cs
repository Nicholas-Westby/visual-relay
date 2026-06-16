using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Implements <see cref="IGitInvoker"/> for tests that need to simulate
/// transient git failures within the <see cref="GitCommitter"/> retry loop.
/// Intercepts git calls whose argument list contains a configured substring
/// and returns synthetic failures for a specified count before falling
/// through to the real git process.
/// </summary>
internal sealed class TransientGitShim : IGitInvoker
{
    private readonly Dictionary<string, int> _failureCounts = new();
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
                return (_exitCode, _stderr, false);
            }
        }

        // Fall through to real git. Strip DEVELOPER_DIR/SDKROOT so xcrun
        // shim cannot resurrect a stale nix-store path inherited from the
        // shell environment.
        return await ProcessCapture.RunAsync("git", argsList,
            rootPath, timeout ?? TimeSpan.FromSeconds(30), ct, environment,
            envRemove: new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" });
    }
}
