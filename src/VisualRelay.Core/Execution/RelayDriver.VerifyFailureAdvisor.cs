using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Appends an identical-failure advisory suffix to <paramref name="exhaustReason"/>
    /// when every red fix-verify attempt carried the same normalized failure text or the
    /// same working-tree hash (manifest files only — not the whole worktree). Returns the
    /// possibly-enriched reason and publishes a single <c>verify_identical_failures</c>
    /// event naming which trigger(s) fired (<c>text</c>, <c>tree</c>, or both).
    /// </summary>
    private async Task<string> TryAppendIdenticalFailureAdvisoryAsync(
        List<(string Reason, string OutputPath, string TreeHash)> verifySignatures,
        string exhaustReason,
        string runId, string rootPath, string taskId,
        RelayStageDefinition stage,
        CancellationToken cancellationToken)
    {
        if (verifySignatures.Count <= 1)
            return exhaustReason;

        var triggers = new List<string>();

        // ── Text trigger: normalized failure signatures are all equal ──────────
        var normalized = verifySignatures
            .Select(s => NormalizeVerifySignature(s.Reason))
            .ToList();
        if (normalized.All(n => n == normalized[0]))
        {
            exhaustReason += " — identical failure across all attempts; likely environment/harness, not the change";
            triggers.Add("text");
        }

        // ── Tree trigger: every red attempt sealed the same tree hash ──────────
        // Equal hash means the MANIFEST files were unchanged across attempts, not
        // the whole worktree (WorkingTreeHash fingerprints manifest files only).
        var treeHashes = verifySignatures.Select(s => s.TreeHash).ToList();
        if (treeHashes.All(h => h == treeHashes[0]))
        {
            exhaustReason += " — tree unchanged across all attempts while verify stayed red; likely environment/harness, not the change";
            triggers.Add("tree");
        }

        if (triggers.Count > 0)
        {
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "warn", "verify_identical_failures", runId, rootPath, taskId,
                stage.Number, stage.Tier,
                Data: new Dictionary<string, string>
                {
                    ["message"] = "All fix-verify attempts produced an identical failure signature",
                    ["trigger"] = string.Join(",", triggers)
                }), cancellationToken);
        }

        return exhaustReason;
    }
}
