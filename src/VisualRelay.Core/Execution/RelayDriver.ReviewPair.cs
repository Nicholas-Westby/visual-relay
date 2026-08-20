using System.Diagnostics;
using System.Text;
using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    private const int TriageMaxTurns = 12;

    private sealed record PairState(
        string PreviousSeal, string TaskHash, double SessionCostUsd,
        int UnknownCostStageCount, RelayTaskOutcome? FlaggedOutcome, string? FixSkipReason);

    // Runs Review (stage 7) and Visual-review (stage 8) concurrently with triage-based routing.
    private async Task<PairState> RunReviewPairAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayConfig config, RelayTaskInput input, StringBuilder ledger,
        List<string> seals, List<StageStatusEntry> statusEntries,
        IReadOnlyList<string> manifest,
        string previousSeal, string taskHash, double sessionCostUsd,
        int unknownCostStageCount, IReadOnlyList<string> taskImagePaths,
        string pinnedSwivalProfileContent, CancellationToken cancellationToken)
    {
        var reviewStage = RelayStages.All[6];    // Stage 7 — Review
        var visualStage = RelayStages.All[7];    // Stage 8 — Visual-review

        var visionConfigured = config.TierProfiles.TryGetValue("vision", out _);

        // Publish stage_start for both.
        await PublishAsync("info", "stage_start", rootPath, runId, taskId, reviewStage, cancellationToken);
        await PublishAsync("info", "stage_start", rootPath, runId, taskId, visualStage, cancellationToken);
        MarkStatus(statusEntries, 7, "Running");
        MarkStatus(statusEntries, 8, "Running");
        await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);

        // Launch Review immediately.
        var reviewTask = RunSingleStageAsync(rootPath, runId, taskId, taskDirectory,
            config, reviewStage, input, ledger, manifest, pinnedSwivalProfileContent, cancellationToken);

        // Launch triage concurrently.
        var triageTask = visionConfigured
            ? RunTriageAsync(rootPath, runId, taskId, taskDirectory, config, input, ledger,
                manifest, pinnedSwivalProfileContent, cancellationToken)
            : Task.FromResult<TriageResult?>(null);

        // Wait for triage first to decide routing.
        var triageResult = await triageTask;

        Task<StageRunResult>? visualTask = null;
        var triageNeeded = triageResult is null
            ? visionConfigured  // Default to needed when triage parsing fails but vision is configured
            : triageResult.VisualReview == "needed";
        if (triageNeeded && visionConfigured)
        {
            var renderOutput = await RunVisualRenderAsync(rootPath, taskDirectory, config, cancellationToken);
            var visualInput = BuildVisualReviewInput(input.Markdown,
                renderOutput.PngPaths, renderOutput.ErrorOutput, taskImagePaths);
            var visualInvocation = BuildInvocation(rootPath, runId, taskId, taskDirectory,
                config, visualStage, input with { Markdown = visualInput },
                ledger, manifest, pinnedSwivalProfileContent: pinnedSwivalProfileContent);
            visualTask = RunStageAsync(visualInvocation, visualStage, taskDirectory, cancellationToken);
        }

        // Await Review.
        var reviewResult = await reviewTask;
        sessionCostUsd += reviewResult.CostUsd;
        if (reviewResult.CostUnknown) unknownCostStageCount++;

        // Fast-visual: visual already finished before review completed.
        // Record visual first (it finished first), then review.
        if (visualTask is { IsCompleted: true })
        {
            var fastVisual = await visualTask;
            sessionCostUsd += fastVisual.CostUsd;
            if (fastVisual.CostUnknown) unknownCostStageCount++;

            if (reviewResult.Check == "red")
            {
                if (reviewResult.Kill is not null)
                {
                    var retryResult = await RunReviewPairStageWithRetryAsync(
                        rootPath, runId, taskId, taskDirectory,
                        config, reviewStage, input, ledger, manifest,
                        pinnedSwivalProfileContent, reviewResult.Kill,
                        cancellationToken);
                    if (retryResult.Check != "red")
                    {
                        // Retry succeeded — record the retried Review. Handle the
                        // sibling: if visual was also red, flag it; otherwise record it.
                        if (fastVisual.Check == "red")
                        {
                            var vReason = BuildReviewFlagReason("Visual-review", fastVisual, rootPath);
                            var vOutcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 8,
                                vReason, fastVisual.Body, statusEntries, cancellationToken);
                            MarkStatus(statusEntries, 7, "Stopped");
                            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
                            await PublishStageDoneAsync(rootPath, runId, taskId, reviewStage,
                                TimeSpan.Zero, null, sessionCostUsd, unknownCostStageCount,
                                cancellationToken, status: "Stopped");
                            return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, vOutcome, null);
                        }
                        (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                            visualStage, fastVisual, ledger, seals, statusEntries, manifest,
                            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                        (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                            reviewStage, retryResult, ledger, seals, statusEntries, manifest,
                            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                        return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, null, FixSkipReason(retryResult.Body, fastVisual.Body));
                    }
                    reviewResult = retryResult;
                }
                var reason = BuildReviewFlagReason("Review", reviewResult, rootPath);
                var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 7,
                    reason, reviewResult.Body, statusEntries, cancellationToken);
                MarkStatus(statusEntries, 8, "Stopped");
                await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
                await PublishStageDoneAsync(rootPath, runId, taskId, visualStage,
                    TimeSpan.Zero, null, sessionCostUsd, unknownCostStageCount,
                    cancellationToken, status: "Stopped");
                return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, outcome, null);
            }
            if (fastVisual.Check == "red")
            {
                if (fastVisual.Kill is not null)
                {
                    var retryResult = await RunReviewPairStageWithRetryAsync(
                        rootPath, runId, taskId, taskDirectory,
                        config, visualStage, input, ledger, manifest,
                        pinnedSwivalProfileContent, fastVisual.Kill,
                        cancellationToken);
                    if (retryResult.Check != "red")
                    {
                        // Retry succeeded — record the retried Visual-review. The
                        // Review sibling (reviewResult) is green here (we are in the
                        // review-green, visual-red branch).
                        (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                            visualStage, retryResult, ledger, seals, statusEntries, manifest,
                            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                        (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                            reviewStage, reviewResult, ledger, seals, statusEntries, manifest,
                            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                        return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, null, FixSkipReason(reviewResult.Body, retryResult.Body));
                    }
                    fastVisual = retryResult;
                }
                var reason = BuildReviewFlagReason("Visual-review", fastVisual, rootPath);
                var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 8,
                    reason, fastVisual.Body, statusEntries, cancellationToken);
                MarkStatus(statusEntries, 7, "Stopped");
                await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
                await PublishStageDoneAsync(rootPath, runId, taskId, reviewStage,
                    TimeSpan.Zero, null, sessionCostUsd, unknownCostStageCount,
                    cancellationToken, status: "Stopped");
                return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, outcome, null);
            }

            // Record visual first (it finished first), then review.
            (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                visualStage, fastVisual, ledger, seals, statusEntries, manifest,
                previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
            (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                reviewStage, reviewResult, ledger, seals, statusEntries, manifest,
                previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);

            return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, null, FixSkipReason(reviewResult.Body, fastVisual.Body));
        }

        // Review finished first (common case).
        // If review produced an invalid result, await the sibling (sibling-survives-failure)
        // then flag — the sibling's result is NOT recorded, matching previous semantics.
        if (reviewResult.Check == "red")
        {
            StageRunResult? siblingResult = null;
            if (visualTask is not null)
            {
                siblingResult = await visualTask;
                sessionCostUsd += siblingResult.CostUsd;
                if (siblingResult.CostUnknown) unknownCostStageCount++;
            }

            if (reviewResult.Kill is not null)
            {
                var retryResult = await RunReviewPairStageWithRetryAsync(
                    rootPath, runId, taskId, taskDirectory,
                    config, reviewStage, input, ledger, manifest,
                    pinnedSwivalProfileContent, reviewResult.Kill,
                    cancellationToken);
                if (retryResult.Check != "red")
                {
                    // Retry succeeded — record review. Handle the sibling
                    // (already awaited): if green, record it; if red, flag it.
                    (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                        reviewStage, retryResult, ledger, seals, statusEntries, manifest,
                        previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                    if (siblingResult is not null)
                    {
                        if (siblingResult.Check == "red")
                        {
                            var vReason = BuildReviewFlagReason("Visual-review", siblingResult, rootPath);
                            var vOutcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 8,
                                vReason, siblingResult.Body, statusEntries, cancellationToken);
                            return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, vOutcome, null);
                        }
                        (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                            visualStage, siblingResult, ledger, seals, statusEntries, manifest,
                            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                    }
                    return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, null, FixSkipReason(retryResult.Body, siblingResult?.Body));
                }
                reviewResult = retryResult;
            }

            var reason = BuildReviewFlagReason("Review", reviewResult, rootPath);
            var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 7,
                reason, reviewResult.Body, statusEntries, cancellationToken);
            // Publish terminal event for the discarded sibling so rehydrate-from-log
            // agrees with status.json.
            if (siblingResult is not null)
            {
                MarkStatus(statusEntries, 8, "Stopped");
                await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
                await PublishStageDoneAsync(rootPath, runId, taskId, visualStage,
                    TimeSpan.Zero, null, sessionCostUsd, unknownCostStageCount,
                    cancellationToken, status: "Stopped");
            }
            return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, outcome, null);
        }

        // Record review immediately — its stage_done fires now, not at the barrier.
        (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
            reviewStage, reviewResult, ledger, seals, statusEntries, manifest,
            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);

        // Await Visual-review if launched.
        StageRunResult? visualResult = null;
        if (visualTask is not null)
        {
            visualResult = await visualTask;
            sessionCostUsd += visualResult.CostUsd;
            if (visualResult.CostUnknown) unknownCostStageCount++;
        }

        if (visualResult is { Check: "red" })
        {
            if (visualResult.Kill is not null)
            {
                var retryResult = await RunReviewPairStageWithRetryAsync(
                    rootPath, runId, taskId, taskDirectory,
                    config, visualStage, input, ledger, manifest,
                    pinnedSwivalProfileContent, visualResult.Kill,
                    cancellationToken);
                if (retryResult.Check != "red")
                {
                    // Retry succeeded — record visual only; review already recorded.
                    (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                        visualStage, retryResult, ledger, seals, statusEntries, manifest,
                        previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                    return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, null, FixSkipReason(reviewResult.Body, retryResult.Body));
                }
                visualResult = retryResult;
            }

            var reason = BuildReviewFlagReason("Visual-review", visualResult, rootPath);
            var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 8,
                reason, visualResult.Body, statusEntries, cancellationToken);
            return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, outcome, null);
        }

        if (visualResult is not null)
        {
            (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                visualStage, visualResult, ledger, seals, statusEntries, manifest,
                previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
        }
        else
        {
            // Record Visual-review as skipped through the shared RecordStageAsync
            // path so it publishes a stage_done{status:Skipped}; the live stage-8
            // card then settles instead of ticking "Running" until next rehydrate.
            // MarkStatusSkipped first so the alreadySkipped guard keeps "Skipped".
            var skipReason = triageResult is { VisualReview: "skip" }
                ? $"_Skipped: {triageResult.Reason}_"
                : "_Skipped: vision tier unconfigured_";
            MarkStatusSkipped(statusEntries, visualStage);
            (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory,
                visualStage, skipReason, "green", null, TimeSpan.Zero, ledger, seals,
                statusEntries, manifest, previousSeal, taskHash, sessionCostUsd,
                unknownCostStageCount, cancellationToken);
        }

        return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, null, FixSkipReason(reviewResult.Body, visualResult?.Body));
    }

    // Triage, render, and visual-input helpers live in
    // RelayDriver.ReviewPairTriage.cs to keep this file under the 300-line guard.
    // RunSingleStageAsync, RunStageAsync, and RecordPairStageAsync live in
    // RelayDriver.ReviewPairRetry.cs together with the retry escalator.
}
