namespace VisualRelay.Core.Execution;

/// <summary>
/// In-memory <see cref="IGitInvoker"/> that returns "not a git repository" for every
/// call — matching the behaviour of <see cref="GitInvoker"/> on a non-repo temp
/// directory. Used as the default in <see cref="RelayDriverDependencies.ForTests"/>
/// so that pipeline tests that don't assert git outcomes avoid spawning real git
/// subprocesses.
/// </summary>
public sealed class NullGitInvoker : IGitInvoker
{
    public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null)
    {
        return Task.FromResult((128, "fatal: not a git repository (or any of the parent directories): .git", false));
    }
}
