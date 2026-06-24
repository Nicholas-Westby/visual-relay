namespace VisualRelay.Core.Init;

// Writes .relay/.gitignore in the target repo so run diagnostics (LLM trace
// dirs, attempt reports, event logs, per-run profile pins) never enter the
// repo's history: they are short-lived working-tree forensics, ~40 MB per
// ten-task drain, while the durable per-task record stays tiny. A blanket
// "*" is safe because the canonical record (ledger.md, status.json,
// manifest.txt, *.seals, and per-stage .input.json/.report.json — final
// attempt only) is force-added by the commit stage (GitCommitter proof
// files use `git add -f`), which gitignore rules do not block.
public static class RelayGitignoreWriter
{
    public static readonly string Content = string.Join('\n',
        "# Maintained by Visual Relay. Run diagnostics (LLM traces, attempt",
        "# reports, event logs, profile pins) are short-lived working-tree",
        "# forensics and stay out of git. The per-task canonical record",
        "# (ledger.md, status.json, manifest.txt, *.seals, and per-stage",
        "# .input.json/.report.json — final attempt only) is force-added",
        "# by the run's commit stage, which these rules do not block.",
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
