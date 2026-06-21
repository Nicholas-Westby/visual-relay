namespace VisualRelay.Core.CommitLint;

/// <summary>
/// Which rule tier the validator enforces.
/// </summary>
public enum RuleTier
{
    /// <summary>All rules: structural plus contextual. Human/dev commits.</summary>
    Human,

    /// <summary>
    /// Structural only — contextual rules (changed-file basenames, path-like
    /// tokens, the disallowed-substring blocklist) are skipped. Used for the
    /// driver's in-run sealed commit, which legitimately names files.
    /// </summary>
    Driver,
}

/// <summary>
/// Inputs the pure validator must not fetch itself: the changed-file basenames,
/// the disallowed substrings, and the rule tier. All git/file IO that produces
/// these lives in the tool, never in the validator.
/// </summary>
/// <param name="Tier">Which rule tier to enforce.</param>
/// <param name="ChangedBasenames">Basenames of the commit's changed files
/// (from <c>git diff --cached --name-only</c> or a per-commit diff).</param>
/// <param name="DisallowedSubstrings">Case-insensitive substrings that block a
/// commit (from <c>disallowed-commit-messages.txt</c>); empty when absent.</param>
public sealed record CommitLintContext(
    RuleTier Tier,
    IReadOnlyList<string> ChangedBasenames,
    IReadOnlyList<string> DisallowedSubstrings)
{
    /// <summary>Context that enforces every rule (human/dev commit).</summary>
    public static CommitLintContext Human(
        IReadOnlyList<string> changedBasenames,
        IReadOnlyList<string> disallowedSubstrings) =>
        new(RuleTier.Human, changedBasenames, disallowedSubstrings);

    /// <summary>
    /// Context that skips the contextual tier (the driver's sealed commit).
    /// </summary>
    public static CommitLintContext Driver(
        IReadOnlyList<string> changedBasenames,
        IReadOnlyList<string> disallowedSubstrings) =>
        new(RuleTier.Driver, changedBasenames, disallowedSubstrings);
}
