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
        int UnknownCostStageCount, RelayTaskOutcome? FlaggedOutcome, bool ReviewFamilyClean);

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
                var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 7,
                    "Review returned an invalid result", reviewResult.Body, statusEntries, cancellationToken);
                return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, outcome, false);
            }
            if (fastVisual.Check == "red")
            {
                var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 8,
                    "Visual-review returned an invalid result", fastVisual.Body, statusEntries, cancellationToken);
                return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, outcome, false);
            }

            // Record visual first (it finished first), then review.
            (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                visualStage, fastVisual, ledger, seals, statusEntries, manifest,
                previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
            (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                reviewStage, reviewResult, ledger, seals, statusEntries, manifest,
                previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);

            return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, null, ReviewFamilyIsClean(reviewResult.Body, fastVisual.Body));
        }

        // Review finished first (common case).
        // If review produced an invalid result, await the sibling (sibling-survives-failure)
        // then flag — the sibling's result is NOT recorded, matching previous semantics.
        if (reviewResult.Check == "red")
        {
            if (visualTask is not null)
            {
                var siblingResult = await visualTask;
                sessionCostUsd += siblingResult.CostUsd;
                if (siblingResult.CostUnknown) unknownCostStageCount++;
            }
            var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 7,
                "Review returned an invalid result", reviewResult.Body, statusEntries, cancellationToken);
            return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, outcome, false);
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
            var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 8,
                "Visual-review returned an invalid result", visualResult.Body, statusEntries, cancellationToken);
            return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, outcome, false);
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

        return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, null, ReviewFamilyIsClean(reviewResult.Body, visualResult?.Body));
    }

    private sealed record TriageResult(string VisualReview, string Reason);
    private sealed record StageRunResult(
        string Body, string? Check, double CostUsd, bool CostUnknown,
        TimeSpan Elapsed, double? TestDurationSeconds);
    private sealed record RenderOutput(IReadOnlyList<string> PngPaths, string? ErrorOutput);

    // Triage, render, and visual-input helpers live in
    // RelayDriver.ReviewPairTriage.cs to keep this file under the 300-line guard.

    private async Task<StageRunResult> RunSingleStageAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayConfig config, RelayStageDefinition stage, RelayTaskInput input,
        StringBuilder ledger, IReadOnlyList<string> manifest,
        string pinnedSwivalProfileContent, CancellationToken cancellationToken)
    {
        var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory,
            config, stage, input, ledger, manifest,
            pinnedSwivalProfileContent: pinnedSwivalProfileContent);
        return await RunStageAsync(invocation, stage, taskDirectory, cancellationToken);
    }

    private async Task<StageRunResult> RunStageAsync(
        StageInvocation invocation, RelayStageDefinition stage, string taskDirectory,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
        stopwatch.Stop();
        var cost = EstimateStageCostCumulative(taskDirectory, stage.Number);
        var costUsd = cost?.CostUsd ?? 0d;
        var costUnknown = cost is null;

        if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
        {
            return new StageRunResult(
                result.RawText,
                "red", costUsd, costUnknown, stopwatch.Elapsed, null);
        }

        return new StageRunResult(result.Json, null, costUsd, costUnknown, stopwatch.Elapsed, null);
    }

    private async Task<(string PreviousSeal, string TaskHash)> RecordPairStageAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayStageDefinition stage, StageRunResult runResult,
        StringBuilder ledger, List<string> seals,
        List<StageStatusEntry> statusEntries, IReadOnlyList<string> manifest,
        string previousSeal, string taskHash, double sessionCostUsd,
        int unknownCostStageCount, CancellationToken cancellationToken)
    {
        var cost = runResult.CostUnknown ? null
            : new RelayCostEstimate("", runResult.CostUsd, true, 0, 0, 0,
                runResult.Elapsed.TotalSeconds);
        return await RecordStageAsync(rootPath, runId, taskId, taskDirectory,
            stage, runResult.Body, runResult.Check, cost,
            runResult.Elapsed, ledger, seals, statusEntries, manifest,
            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount,
            cancellationToken, runResult.TestDurationSeconds);
    }
}
