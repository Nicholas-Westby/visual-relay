namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>One fix-verify attempt's convergence fingerprint.</summary>
    private readonly record struct VerifyAttemptFingerprint(string TreeHash, string DistilledReason);

    /// <summary>
    /// True when this red attempt is provably non-convergent: the working-tree
    /// fingerprint and the distilled failure are BOTH identical to the prior
    /// attempt, so looping cannot improve the verdict. LIMITATION: the tree hash
    /// (<see cref="WorkingTreeHash"/>) covers only the MANIFEST files' contents, so
    /// an agent edit OUTSIDE the manifest is invisible here — the guard may then
    /// bail conservatively. Accepted: the alternative is burning every attempt, and
    /// Task 8's full-tree isolation makes the authoritative gate verify the right
    /// code regardless.
    /// </summary>
    private static bool IsNonConvergent(int attempt, string check, VerifyAttemptFingerprint current, VerifyAttemptFingerprint? prior) =>
        attempt >= 2 && check == "red" && prior is { } p
        && current.TreeHash == p.TreeHash
        && current.DistilledReason == p.DistilledReason;
}
