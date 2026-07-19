using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Wraps an inner <see cref="IGitInvoker"/> and records every argument vector
/// passed to <see cref="RunAsync"/>. Use with <see cref="GitSim.GitSim"/> to
/// prove that every git probe in a driver run flows through the injected invoker
/// rather than a private <c>new GitInvoker()</c> fallback.
/// </summary>
public sealed class RecordingGitInvoker : IGitInvoker
{
    private readonly IGitInvoker _inner;
    private readonly List<string[]> _calls = [];

    public RecordingGitInvoker(IGitInvoker inner)
    {
        _inner = inner;
    }

    /// <summary>Every argument vector recorded, in call order.</summary>
    public IReadOnlyList<string[]> Calls => _calls;

    /// <summary>True when any recorded call's argument vector contains every element of <paramref name="args"/>.</summary>
    public bool RecordedCall(string[] args) =>
        _calls.Any(c => args.All(a => c.Contains(a, StringComparer.Ordinal)));

    /// <summary>Count of recorded calls whose argument vector contains every element of <paramref name="args"/>.</summary>
    public int CallCount(string[] args) =>
        _calls.Count(c => args.All(a => c.Contains(a, StringComparer.Ordinal)));

    public async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null)
    {
        var args = arguments.ToArray();
        _calls.Add(args);
        return await _inner.RunAsync(rootPath, arguments, cancellationToken,
            timeout, environment, killToken, onActivity);
    }
}
