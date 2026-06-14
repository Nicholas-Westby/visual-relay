namespace VisualRelay.Core.Execution;

internal sealed record GitCommitResult(bool Success, string? CommitSha, string? Error)
{
    public static GitCommitResult Committed(string sha) => new(true, sha, null);
    public static GitCommitResult Failed(string error) => new(false, null, error);
}
