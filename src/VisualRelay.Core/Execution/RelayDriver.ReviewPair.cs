using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    private const int TriageMaxTurns = 12;

    private sealed record PairState(
        string PreviousSeal, string TaskHash, double SessionCostUsd,
        int UnknownCostStageCount, RelayTaskOutcome? FlaggedOutcome);

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
            var visualInput = BuildVisualReviewInput(input.Markdown, taskDirectory,
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

        // Await Visual-review if launched.
        StageRunResult? visualResult = null;
        if (visualTask is not null)
        {
            visualResult = await visualTask;
            sessionCostUsd += visualResult.CostUsd;
            if (visualResult.CostUnknown) unknownCostStageCount++;
        }

        // If either review produced an invalid result, flag after both finish
        // (sibling-survives-failure: the sibling's completed result is preserved
        // in the ledger up to this point, but the run is aborted).
        if (reviewResult.Check == "red")
        {
            var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 7,
                "Review returned an invalid result", reviewResult.Body, statusEntries, cancellationToken);
            return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, outcome);
        }
        if (visualResult is { Check: "red" })
        {
            var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 8,
                "Visual-review returned an invalid result", visualResult.Body, statusEntries, cancellationToken);
            return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, outcome);
        }

        // Serialize ledger writes: Review first, then Visual-review.
        (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
            reviewStage, reviewResult, ledger, seals, statusEntries, manifest,
            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);

        if (visualResult is not null)
        {
            (previousSeal, taskHash) = await RecordPairStageAsync(rootPath, runId, taskId, taskDirectory,
                visualStage, visualResult, ledger, seals, statusEntries, manifest,
                previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
        }
        else
        {
            // Record Visual-review as skipped.
            var skipReason = triageResult is { VisualReview: "skip" }
                ? $"_Skipped: {triageResult.Reason}_"
                : "_Skipped: vision tier unconfigured_";
            AppendLedgerSection(ledger, visualStage, skipReason);
            MarkStatusSkipped(statusEntries, visualStage);
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
            var h = Hashing.Sha256Hex("8", visualStage.Name, skipReason);
            var seal = Hashing.Sha256Hex(previousSeal, "8", DateTimeOffset.UtcNow.ToString("O"), h, string.Empty, string.Empty);
            seals.Add(SerializeSeal(8, h, string.Empty, seal, null));
            previousSeal = seal; taskHash = seal;
            await WriteArtifactsAsync(taskDirectory, taskId, ledger.ToString(), seals, cancellationToken);
        }

        return new PairState(previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, null);
    }

    private sealed record TriageResult(string VisualReview, string Reason);
    private sealed record StageRunResult(
        string Body, string? Check, double CostUsd, bool CostUnknown,
        Stopwatch Stopwatch, double? TestDurationSeconds);
    private sealed record RenderOutput(IReadOnlyList<string> PngPaths, string? ErrorOutput);

    private async Task<TriageResult?> RunTriageAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayConfig config, RelayTaskInput input, StringBuilder ledger,
        IReadOnlyList<string> manifest, string pinnedSwivalProfileContent,
        CancellationToken cancellationToken)
    {
        var triagePrompt =
            "You are a triage agent. Decide whether a visual review of rendered output " +
            "would benefit this task change. Consider: UI markup/styles/layout in any " +
            "framework, web frontends, terminal UI, images or other visual assets, " +
            "charts, generated documents. If genuinely uncertain, prefer \"needed\" — " +
            "a vision pass costs cents; a missed visual defect costs a run.";

        var triageStage = new RelayStageDefinition(0, "Visual-triage", "cheap", "llm",
            "some", "git,ls,cat,grep,find,head,tail,wc", triagePrompt,
            """End your reply with a single fenced ```json block, nothing after it, matching: { "visualReview": "needed"|"skip", "reason": string }""");

        var triageInvocation = BuildInvocation(rootPath, runId, taskId, taskDirectory,
            config, triageStage, input, ledger, manifest,
            pinnedSwivalProfileContent: pinnedSwivalProfileContent);
        triageInvocation = triageInvocation with
        {
            Tier = "cheap",
            MaxTurns = TriageMaxTurns,
            MaxSelfEscalations = 0
        };

        var result = await _dependencies.SubagentRunner.RunAsync(triageInvocation, cancellationToken);
        if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(result.Json);
            var root = doc.RootElement;
            var visualReview = root.TryGetProperty("visualReview", out var vr) && vr.GetString() is { } v
                ? v : "needed"; // Default to needed when unrecognized (bias toward review)
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return new TriageResult(visualReview, reason);
        }
        catch
        {
            // Default to needed on parse failure (bias toward review).
            return new TriageResult("needed", "triage parsing failed; defaulting to needed");
        }
    }

    private async Task<RenderOutput> RunVisualRenderAsync(
        string rootPath, string taskDirectory,
        RelayConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.VisualRenderCmd))
        {
            return new RenderOutput([], null);
        }

        var renderDir = Path.Combine(taskDirectory, "visual-review");
        Directory.CreateDirectory(renderDir);
        var cmd = config.VisualRenderCmd.Replace("{outDir}", renderDir);

        try
        {
            var testResult = await _dependencies.TestRunner.RunAsync(rootPath, cmd, cancellationToken);
            if (testResult.TimedOut)
                return new RenderOutput([], $"Render command timed out: {testResult.Output}");

            var pngs = Directory.Exists(renderDir)
                ? Directory.GetFiles(renderDir, "*.png").Select(p => Path.GetRelativePath(rootPath, p)).ToList()
                : (IReadOnlyList<string>)[];

            if (testResult.ExitCode != 0)
                return new RenderOutput(pngs, $"Render command failed (exit {testResult.ExitCode}): {testResult.Output}");

            return new RenderOutput(pngs, null);
        }
        catch (Exception ex)
        {
            return new RenderOutput([], $"Render command exception: {ex.Message}");
        }
    }

    private static string BuildVisualReviewInput(string markdown, string taskDirectory,
        IReadOnlyList<string> renderPngs, string? renderError,
        IReadOnlyList<string> taskImagePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine(markdown);
        sb.AppendLine();
        sb.AppendLine("## Images to review");

        if (renderPngs.Count > 0)
        {
            sb.Append("### Rendered screenshots\n");
            sb.AppendJoin('\n', renderPngs.Select(p => $"- `{p}`  ← open with view_image"));
            sb.AppendLine();
        }
        else if (renderError is not null)
        {
            sb.Append("### Render errors (review as findings)\n");
            sb.AppendLine(renderError);
        }
        else sb.Append("Fresh renders are unavailable.\n");

        var taskPngs = taskImagePaths.Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        if (taskPngs.Count > 0)
        {
            sb.Append("\n### Task image attachments\n");
            sb.AppendJoin('\n', taskPngs.Select(p => $"- `{p}`  ← open with view_image"));
            sb.AppendLine();
        }
        else if (renderPngs.Count == 0) sb.Append("No images available for review.\n");

        sb.Append("\n## Instructions\nOpen each listed image with view_image. " +
            "Identify concrete visual defects. If nothing visual is wrong, " +
            "return {\"verdict\":\"pass\",\"issues\":[]} immediately.\n");
        return sb.ToString();
    }

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
        var cost = EstimateStageCostCumulative(taskDirectory, stage.Number);
        var costUsd = cost?.CostUsd ?? 0d;
        var costUnknown = cost is null;

        if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
        {
            return new StageRunResult(
                result.RawText ?? result.Error ?? "invalid subagent result",
                "red", costUsd, costUnknown, stopwatch, null);
        }

        return new StageRunResult(result.Json, null, costUsd, costUnknown, stopwatch, null);
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
                runResult.Stopwatch.Elapsed.TotalSeconds);
        return await RecordStageAsync(rootPath, runId, taskId, taskDirectory,
            stage, runResult.Body, runResult.Check, cost,
            runResult.Stopwatch, ledger, seals, statusEntries, manifest,
            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount,
            cancellationToken, runResult.TestDurationSeconds);
    }
}
