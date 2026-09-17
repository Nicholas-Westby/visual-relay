using System.Text;
using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    private const int FixVerifyReviewMaxTurns = 16;

    /// <summary>
    /// Reviews what Fix-verify changed outside the plan. Review (7) and Visual-review
    /// (8) run before Verify (10), so a Fix-verify edit reached Commit with nobody
    /// looking at it: on i18next both tasks' agents pinned a time zone in the
    /// project's test configuration to get a green suite, and one commit carried it.
    /// Paths in the plan's manifest (plus anything stage 11 amended into it) are
    /// already reviewed; the rest get the Review prompt once more on their diff alone.
    /// Returns a flagged outcome when the reviewer asks for changes, else null.
    /// </summary>
    private async Task<RelayTaskOutcome?> ReviewFixVerifyEditsAsync(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        RelayStageDefinition stage,
        RelayTaskInput input,
        StringBuilder ledger,
        List<StageStatusEntry> statusEntries,
        IReadOnlyList<string> manifest,
        IReadOnlySet<string> changesBeforeFixVerify,
        string fixVerifyBody,
        CancellationToken cancellationToken)
    {
        var after = await WorktreeChanges.ListAsync(
            rootPath, config.TasksDir, _dependencies.GitInvoker, cancellationToken);

        // One spelling for both sides, the same resolution every model-written path
        // list gets, so an amended entry can never miss the file it names.
        var reviewed = new HashSet<string>(
            ManifestPaths.ResolveAll(rootPath, manifest.Concat(AmendedManifest(fixVerifyBody))).Resolved,
            StringComparer.Ordinal);

        var unreviewed = after
            .Where(path => !changesBeforeFixVerify.Contains(path) && !reviewed.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (unreviewed.Count == 0)
            return null;

        var pathList = string.Join(", ", unreviewed);
        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "fix_verify_unreviewed_edits", runId, rootPath, taskId,
            stage.Number, Data: new Dictionary<string, string> { ["paths"] = pathList }), cancellationToken);

        var verdict = await RunFixVerifyReviewStageAsync(
            rootPath, runId, taskId, taskDirectory, config, input, ledger, manifest,
            unreviewed, fixVerifyBody, cancellationToken);
        if (verdict is null || verdict.Value.Verdict != "changes")
            return null;

        var reason = $"fix-verify edited {pathList} outside the plan";
        if (!string.IsNullOrWhiteSpace(verdict.Value.Issues))
            reason += $": {verdict.Value.Issues}";
        return await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
            reason, verdict.Value.Body, statusEntries, cancellationToken);
    }

    private async Task<(string Verdict, string Issues, string Body)?> RunFixVerifyReviewStageAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayConfig config, RelayTaskInput input, StringBuilder ledger,
        IReadOnlyList<string> manifest, IReadOnlyList<string> paths, string fixVerifyBody,
        CancellationToken cancellationToken)
    {
        var review = RelayStages.All[6];  // Stage 7 — Review, whose prompt and contract this reuses
        var reviewStage = new RelayStageDefinition(
            0, "Fix-verify-review", "cheap", "llm", "some", review.Commands,
            review.SystemPrompt + " "
            + "You are reviewing ONLY the edits the Fix-verify stage made to files the plan never "
            + "listed, shown below. Judge whether each edit is a legitimate part of this task or a "
            + "way of making the suite pass that nobody asked for (a pinned environment value, a "
            + "weakened or disabled test, a changed test configuration). Return \"pass\" when every "
            + "edit belongs to the task.",
            review.OutputContract);

        var diff = await DiffForPathsAsync(rootPath, paths, cancellationToken);
        var body = new StringBuilder(input.Markdown);
        body.AppendLine();
        body.AppendLine();
        body.AppendLine("## Fix-verify summary");
        body.AppendLine(fixVerifyBody);
        body.AppendLine();
        body.AppendLine("## Edits outside the plan");
        foreach (var path in paths)
            body.AppendLine($"- `{path}`");
        body.AppendLine();
        body.AppendLine("```diff");
        body.AppendLine(diff);
        body.AppendLine("```");

        var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory, config,
            reviewStage, input with { Markdown = body.ToString() }, ledger, manifest);
        invocation = invocation with { Tier = "cheap", MaxTurns = FixVerifyReviewMaxTurns };

        var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
        if (!result.IsValid || !TryParseContractJson(result.Json, out var json, out _))
            return null;

        var verdict = ReadOptionalString(json, "verdict") ?? "pass";
        var issues = string.Join("; ", ReadStringArray(json, "issues"));
        return (verdict, issues, result.Json ?? string.Empty);
    }

    /// <summary>
    /// The diff of the named paths against HEAD. An untracked file shows nothing there,
    /// so it is named as a new file and the reviewer reads it with its own tools.
    /// </summary>
    private async Task<string> DiffForPathsAsync(
        string rootPath, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        List<string> arguments = ["-c", "core.quotePath=false", "diff", "HEAD", "--"];
        arguments.AddRange(paths);
        var result = await _dependencies.GitInvoker.RunAsync(rootPath, arguments, cancellationToken);
        return result is { ExitCode: 0, TimedOut: false } && !string.IsNullOrWhiteSpace(result.Output)
            ? result.Output
            : "(no diff against HEAD; read each file listed above to see what it now holds)";
    }

    private static IEnumerable<string> AmendedManifest(string fixVerifyBody) =>
        TryParseContractJson(fixVerifyBody, out var json, out _)
            ? ReadStringArray(json, "amendManifest")
            : [];
}
