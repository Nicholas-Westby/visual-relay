namespace VisualRelay.Core.Execution;

/// <summary>
/// Classifies git failure signatures as deterministic (will never succeed on retry)
/// vs potentially transient. Only git itself, <see cref="NullGitInvoker"/>, and
/// GitSim emit the recognised signatures, so one classifier behaves identically
/// across all three backends.
/// </summary>
public static class GitFailureClassifier
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="exitCode"/> is nonzero AND
    /// <paramref name="output"/> contains (ordinal, ignore-case) one of the
    /// recognised deterministic signatures. The set is deliberately conservative:
    /// unknown failures remain retryable.
    /// </summary>
    /// <param name="exitCode">The git process exit code.</param>
    /// <param name="output">Combined stdout/stderr from the git process.</param>
    public static bool IsDeterministic(int exitCode, string output)
    {
        if (exitCode == 0)
            return false;

        return output.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
            || output.Contains("invalid reference", StringComparison.OrdinalIgnoreCase);
    }
}
