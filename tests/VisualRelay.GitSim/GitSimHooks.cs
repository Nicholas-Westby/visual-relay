namespace VisualRelay.GitSim;

/// <summary>
/// What the simulated <c>pre-commit</c> hook is shown for a <c>commit -m</c>: the
/// paths staged for the commit, the full commit message (subject + trailers, exactly
/// as passed to <c>-m</c>), and the environment dict the invocation carried (so a
/// hook can gate on <c>RELAY_COMMIT_TOKEN</c>/<c>RELAY_NONCE</c> like the real one).
/// </summary>
public sealed record GitSimCommitRequest(
    IReadOnlyList<string> StagedPaths,
    string Message,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// A hook decision. <see cref="Accept"/> lets the commit proceed; <see cref="Reject"/>
/// carries the diagnostic that lands in the command's combined output with a non-zero
/// exit — exactly the failure shape <c>GitCommitter.CommitAsync</c> parses.
/// </summary>
public sealed record GitSimHookVerdict(bool Accepted, string Message)
{
    public static GitSimHookVerdict Accept { get; } = new(true, string.Empty);
    public static GitSimHookVerdict Reject(string message) => new(false, message);
}
