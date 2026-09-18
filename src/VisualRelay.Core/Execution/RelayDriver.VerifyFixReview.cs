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
        var unreviewed = await UnreviewedFixVerifyEditsAsync(
            rootPath, runId, taskId, config, stage.Number, manifest, changesBeforeFixVerify,
            fixVerifyBody, cancellationToken);
        if (unreviewed.Count == 0)
            return null;

        var pathList = string.Join(", ", unreviewed);
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

    /// <summary>
    /// The files Fix-verify touched that neither the plan nor the loop's starting
    /// listing accounts for, announced as <c>fix_verify_unreviewed_edits</c>.
    /// </summary>
    private async Task<IReadOnlyList<string>> UnreviewedFixVerifyEditsAsync(
        string rootPath, string runId, string taskId, RelayConfig config, int stageNumber,
        IReadOnlyList<string> manifest, IReadOnlySet<string> changesBeforeFixVerify,
        string fixVerifyBody, CancellationToken cancellationToken)
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
            return unreviewed;

        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "fix_verify_unreviewed_edits", runId, rootPath, taskId,
            stageNumber, Data: new Dictionary<string, string>
            {
                ["paths"] = string.Join(", ", unreviewed),
            }), cancellationToken);
        return unreviewed;
    }

    /// <summary>
    /// The same listing, for the flag that ends an exhausted ladder. The review above
    /// only runs on a GREEN attempt, so a run that never went green used to hand a
    /// human a bundle holding edits nobody asked for without saying so — measured on
    /// i18next, where three Fix-verify attempts failed on the environment and the
    /// bundle carried a time zone pinned into two test files that the plan never
    /// named. The flag reason for that run said the change was probably fine and the
    /// harness was at fault, which is exactly the sentence that stops a reader
    /// looking. No model call: the run has already failed, and what the reader needs
    /// is the list, not a verdict on it.
    /// </summary>
    private async Task<string> DescribeUnreviewedFixVerifyEditsAsync(
        string rootPath, string runId, string taskId, RelayConfig config, int stageNumber,
        IReadOnlyList<string> manifest, IReadOnlySet<string> changesBeforeFixVerify,
        string? fixVerifyBody, CancellationToken cancellationToken)
    {
        var unreviewed = await UnreviewedFixVerifyEditsAsync(
            rootPath, runId, taskId, config, stageNumber, manifest, changesBeforeFixVerify,
            fixVerifyBody ?? string.Empty, cancellationToken);
        return unreviewed.Count == 0
            ? string.Empty
            : $" Fix-verify also edited {string.Join(", ", unreviewed)} outside the plan; "
              + "nothing reviewed those edits.";
    }

    /// <summary>
    /// What the commit is about to carry that the plan never named, announced as
    /// <c>commit_unplanned_edits</c>.
    /// <para>
    /// The two checks above both hang off Fix-verify: one runs when that stage goes
    /// green, the other when its ladder is exhausted. Measured on i18next: a RESUME
    /// whose Verify passed on the first attempt SKIPS Fix-verify entirely, so neither
    /// fired, and two test files a previous run had edited outside the plan went into
    /// the commit unreviewed and unmentioned. The edits were made by a stage that was
    /// not running any more. So this one hangs off the commit instead, which is where
    /// the edits stop being a working tree and start being history, and it does not
    /// care which stage made them or whether that stage ran.
    /// </para>
    /// <para>
    /// It reports rather than refuses. Two clean runs on gorilla/mux committed exactly
    /// the manifest plus the retired task file, so this is silent on an ordinary run;
    /// making it block on the strength of one observed case would be a policy change
    /// the evidence does not carry yet.
    /// </para>
    /// </summary>
    private async Task ReportUnplannedCommitEditsAsync(
        string rootPath, string runId, string taskId, RelayConfig config,
        IReadOnlyList<string> manifest, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> unplanned;
        try
        {
            var changed = await WorktreeChanges.ListAsync(
                rootPath, config.TasksDir, _dependencies.GitInvoker, cancellationToken);
            var planned = new HashSet<string>(
                ManifestPaths.ResolveAll(rootPath, manifest).Resolved, StringComparer.Ordinal);
            unplanned = [.. changed.Where(path => !planned.Contains(path))
                .OrderBy(path => path, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Never let a report stop a commit that has already passed its gates.
            return;
        }

        if (unplanned.Count == 0)
            return;

        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "commit_unplanned_edits", runId, rootPath, taskId, 12,
            Data: new Dictionary<string, string>
            {
                ["paths"] = string.Join(", ", unplanned),
            }), cancellationToken);
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
