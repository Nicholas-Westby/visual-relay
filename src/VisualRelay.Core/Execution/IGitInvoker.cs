namespace VisualRelay.Core.Execution;

/// <summary>
/// Injected git process factory. Production code receives a <see cref="GitInvoker"/>
/// that pins a stable binary and sanitizes the environment; tests pass a fake
/// without touching any process-global static.
/// </summary>
public interface IGitInvoker
{
    /// <summary>
    /// Run a git command rooted at <paramref name="rootPath"/> and return
    /// the exit code, combined stdout+stderr, and a timed-out flag.
    /// </summary>
    Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null);
}
