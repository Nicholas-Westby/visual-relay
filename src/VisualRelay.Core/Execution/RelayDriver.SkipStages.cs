using System.Text;
using System.Text.Json;
using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Mechanical stage-skip rules (no LLM, no time-based reasoning): the driver
// reads the structured verdicts it already parses and decides whether the Fix
// (9) and Fix-verify (11) stages need to launch at all.
//
//   GENERALIZED CONTRACT — skip Fix (9) only when EVERY review-family stage
//   that feeds it reported a clean pass OR was itself skipped. The review family
//   is Review (7) + Visual-review (8); a stage added to that family later MUST be
//   AND-ed into FixSkipReason, never allowed to bypass it. Fix-verify (11)
//   skips symmetrically when Verify (10) is green. Both rules FAIL OPEN: any
//   non-pass, non-empty issues, or malformed/unparseable verdict runs the stage
//   exactly as before — the skip never triggers on uncertainty.
public sealed partial class RelayDriver
{
    /// <summary>
    /// The ledger reason for skipping Fix (9), or null when Fix must run.
    /// <paramref name="reviewBody"/> must be a clean pass; Visual-review must have
    /// been skipped (<paramref name="visualBody"/> is null), be a clean pass, or be
    /// <c>unassessable</c> — a render that never showed the subject leaves no defect
    /// to fix, but it is reported with its own reason so a clean visual review is
    /// never claimed on its behalf.
    /// </summary>
    private static string? FixSkipReason(string reviewBody, string? visualBody)
    {
        if (!IsCleanReviewVerdict(reviewBody))
            return null;
        if (visualBody is null || IsCleanReviewVerdict(visualBody))
            return "_Skipped: review passed with no issues._";
        return IsUnassessableVisualVerdict(visualBody)
            ? "_Skipped: review passed; visual review was unassessable, so nothing to fix._"
            : null;
    }

    /// <summary>
    /// True when a Visual-review verdict is <c>unassessable</c>: the subject of the
    /// task never appeared in the supplied renders, so the stage neither passed nor
    /// found a defect. Deliberately verdict-only — any issues it did list stay in
    /// ledger.md and are not fixable from a render that did not show the subject.
    /// </summary>
    private static bool IsUnassessableVisualVerdict(string? body) =>
        TryParseContractJson(body, out var json, out _)
        && json.TryGetProperty("verdict", out var verdict)
        && verdict.ValueKind == JsonValueKind.String
        && string.Equals(verdict.GetString(), "unassessable", StringComparison.Ordinal);

    /// <summary>
    /// True only when a Review/Visual-review verdict is a well-formed clean pass:
    /// the contract parses to an object, <c>verdict == "pass"</c>, and <c>issues</c>
    /// is an empty array. Any other shape — non-pass verdict, missing or non-empty
    /// issues, or malformed JSON — returns false so the caller fails open to
    /// running Fix. Reuses the shared <see cref="TryParseContractJson"/>; no
    /// second ad-hoc parse.
    /// </summary>
    private static bool IsCleanReviewVerdict(string? body)
    {
        if (!TryParseContractJson(body, out var json, out _))
            return false;
        if (!json.TryGetProperty("verdict", out var verdict)
            || verdict.ValueKind != JsonValueKind.String
            || !string.Equals(verdict.GetString(), "pass", StringComparison.Ordinal))
            return false;
        return json.TryGetProperty("issues", out var issues)
            && issues.ValueKind == JsonValueKind.Array
            && issues.GetArrayLength() == 0;
    }

    /// <summary>
    /// Records the Fix stage (9) as skipped under <paramref name="reason"/> — ledger
    /// section, seal, Skipped status,
    /// and a terminal <c>stage_done{status:Skipped}</c> — through the shared
    /// <see cref="RecordStageAsync"/> path so the queue card settles and resume
    /// treats it as complete. A 0-second, $0 stage; no subagent is launched.
    /// </summary>
    private async Task<(string PreviousSeal, string TaskHash)> RecordFixSkipAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayStageDefinition stage, string reason, StringBuilder ledger, List<string> seals,
        List<StageStatusEntry> statusEntries, IReadOnlyList<string> manifest,
        string previousSeal, string taskHash, double sessionCostUsd,
        int unknownCostStageCount, CancellationToken cancellationToken)
    {
        MarkStatusSkipped(statusEntries, stage);
        return await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage,
            reason, "green", null,
            TimeSpan.Zero, ledger, seals, statusEntries, manifest,
            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount,
            cancellationToken);
    }

    /// <summary>
    /// Records Verify (10) green, then skips Fix-verify (11) — nothing to fix —
    /// through <see cref="RecordStageAsync"/> so stage 11's seal and Skipped status
    /// settle and resume treats it as complete. Symmetric to the Fix (9) skip on a
    /// clean review family.
    /// </summary>
    private async Task<(string PreviousSeal, string TaskHash)> RecordVerifyGreenSkipFixVerifyAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayStageDefinition verifyStage, string body, string? check, RelayCostEstimate? cost,
        TimeSpan elapsed, StringBuilder ledger, List<string> seals,
        List<StageStatusEntry> statusEntries, IReadOnlyList<string> manifest,
        string previousSeal, string taskHash, double sessionCostUsd,
        int unknownCostStageCount, double? testDurationSeconds, CancellationToken cancellationToken)
    {
        (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory,
            verifyStage, body, check, cost, elapsed, ledger, seals, statusEntries, manifest,
            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds);
        var fixVerifyStage = RelayStages.All[10]; // Stage 11 — Fix-verify
        MarkStatusSkipped(statusEntries, fixVerifyStage);
        return await RecordStageAsync(rootPath, runId, taskId, taskDirectory, fixVerifyStage,
            "_Skipped: Verify passed; nothing to fix._", "green", null, TimeSpan.Zero,
            ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd,
            unknownCostStageCount, cancellationToken);
    }
}
