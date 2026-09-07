namespace VisualRelay.Core.Init;

// Writes .relay/.gitignore in the target repo so a run's bookkeeping never
// enters the repo's history: LLM trace dirs, attempt reports, event logs and
// per-run profile pins are short-lived working-tree forensics (~40 MB per
// ten-task drain), and even the durable per-task record is Visual Relay's
// working state, not the repo's. The commit stage stages nothing under
// .relay/ either way; the negations exist only so an author who WANTS the
// config in git can add it by hand.
public static class RelayGitignoreWriter
{
    public static readonly string Content = string.Join('\n',
        "# Maintained by Visual Relay. Everything under .relay/ is Visual Relay's",
        "# own working state — traces, attempt reports, event logs, the per-task",
        "# ledger/status/manifest/seals — and stays out of git; the commit stage",
        "# never stages it. The negations below only let an author track the",
        "# config by hand if they want it shared.",
        "*",
        "!.gitignore",
        "!config.json") + "\n";

    /// <summary>
    /// Writes <c>.relay/.gitignore</c> when the <c>.relay</c> directory
    /// exists and has no .gitignore yet. Returns true when the file was
    /// written. An existing file is never modified — a repo owner's hand
    /// edits win over the default policy.
    /// </summary>
    public static bool EnsureWritten(string rootPath)
    {
        var relayDir = Path.Combine(rootPath, ".relay");
        if (!Directory.Exists(relayDir))
        {
            return false;
        }

        var path = Path.Combine(relayDir, ".gitignore");
        if (File.Exists(path))
        {
            return false;
        }

        File.WriteAllText(path, Content);
        return true;
    }
}
