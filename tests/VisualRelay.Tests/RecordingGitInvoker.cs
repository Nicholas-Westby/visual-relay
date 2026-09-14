using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Wraps an inner <see cref="IGitInvoker"/> and records every argument vector
/// passed to <see cref="RunAsync"/>. Use with <see cref="GitSim.GitSim"/> to
/// prove that every git probe in a driver run flows through the injected invoker
/// rather than a private <c>new GitInvoker()</c> fallback.
/// </summary>
/// <param name="inner">The invoker every recorded call is forwarded to.</param>
public sealed class RecordingGitInvoker(IGitInvoker inner) : IGitInvoker
{
    private readonly List<(string[] Args, IReadOnlyDictionary<string, string>? Environment)> _calls = [];

    /// <summary>True when any recorded call's argument vector contains every element of <paramref name="args"/>.</summary>
    public bool RecordedCall(string[] args) =>
        _calls.Any(c => args.All(a => c.Args.Contains(a, StringComparer.Ordinal)));

    /// <summary>
    /// The environment handed to the first call whose argument vector starts with
    /// <paramref name="prefix"/>; null when there was no such call or it carried none.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentOf(params string[] prefix) =>
        _calls.FirstOrDefault(c => c.Args.Take(prefix.Length).SequenceEqual(prefix, StringComparer.Ordinal)).Environment;

    public async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null)
    {
        // Materialize once and forward the materialized array: `arguments` may be
        // a lazy sequence, so enumerating it here for the recording AND again in
        // the inner invoker would repeat its side effects — or hand the inner
        // invoker an already-drained iterator and lose the git arguments entirely.
        var args = arguments.ToArray();
        _calls.Add((args, environment is null ? null : new Dictionary<string, string>(environment)));
        return await inner.RunAsync(rootPath, args, cancellationToken,
            timeout, environment, killToken, onActivity);
    }
}
