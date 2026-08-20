using System.Text;
using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
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
                ? v : "needed";
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return new TriageResult(visualReview, reason);
        }
        catch
        {
            return new TriageResult("needed", "triage parsing failed; defaulting to needed");
        }
    }

    private async Task<RenderOutput> RunVisualRenderAsync(
        string rootPath, string taskDirectory,
        RelayConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.VisualRenderCmd))
            return new RenderOutput([], null);

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

    private static string BuildVisualReviewInput(string markdown,
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
            "return {\"verdict\":\"pass\",\"issues\":[]} immediately. If the task's " +
            "subject is not shown in any of these images, return " +
            "{\"verdict\":\"unassessable\",\"issues\":[...]} rather than pass.\n");
        return sb.ToString();
    }
}
