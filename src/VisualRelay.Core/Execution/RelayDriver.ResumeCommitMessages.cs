namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// The commit messages of the last Verify section in <paramref name="ledger"/>, for a run that
    /// did not reach Verify itself: they are read only while Verify runs, so a task resumed at the
    /// commit stage (on the Windows arm, after a fresh distro's missing git identity failed its
    /// commit) landed with the generic "chore(relay)" subject. Empty when no section has any.
    /// </summary>
    internal static IReadOnlyList<string> CommitMessagesFromLedger(string ledger)
    {
        var heading = $"## Stage 10 - {RelayStages.All[9].Name}";
        var start = ledger.LastIndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
            return [];

        var bodyStart = start + heading.Length;
        var next = ledger.IndexOf("\n## Stage ", bodyStart, StringComparison.Ordinal);
        if (!TryParseContractJson(next < 0 ? ledger[bodyStart..] : ledger[bodyStart..next], out var json, out _))
            return [];

        var messages = ReadStringArray(json, "commitMessages");
        return messages.Count > 0 || ReadOptionalString(json, "commitMessage") is not { } legacy ? messages : [legacy];
    }
}
