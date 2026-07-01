using System.Text.Json;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Records the outcome of <see cref="FixTaskAuthorRunner.RunAsync"/>.
/// </summary>
public sealed record FixTaskAuthorOutcome(
    bool Success,
    string? Markdown,
    string? Summary,
    string? Slug,
    string? Error);

/// <summary>
/// Calls a subagent to author a new llm-task markdown from a failed run's
/// diagnostics. No worktree — this is a read-only prompt-and-parse step.
/// Modeled on <see cref="TaskRewriteRunner"/> but simpler.
/// </summary>
public static class FixTaskAuthorRunner
{
    private const string SystemPrompt = """
        You are a task-authoring assistant. Given the context of a failed Visual Relay run,
        output a new llm-task markdown that fixes the root causes so the failures stop
        recurring. Make flaky / non-deterministic tests deterministic; fix root causes;
        address the enabler where possible. Never weaken, skip, or delete tests.

        Output a single JSON object — no surrounding text or code fences — with these keys:
        - "markdown": the full task markdown (title, problem, root cause, fix, constraints).
        - "summary": a one-line summary of what the task addresses (max 120 chars).
        - "slug": a kebab-case slug derived from the task title (lowercase letters, digits, and hyphens only).
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Runs the subagent to author a fix task from the failed-run diagnostics
    /// in <paramref name="taskDirectory"/>.
    /// </summary>
    public static async Task<FixTaskAuthorOutcome> RunAsync(
        string rootPath,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        ISubagentRunner runner,
        CancellationToken ct)
    {
        // Gather context.
        var context = FailedRunContextReader.Read(taskDirectory);

        var prompt = BuildPrompt(taskId, context);

        var traceDir = Path.Combine(rootPath, ".relay", taskId, "fix-task");
        Directory.CreateDirectory(traceDir);
        var reportFile = Path.Combine(traceDir, "fix-task.log");

        var stageDef = new RelayStageDefinition(
            Number: 0,
            Name: "FixTaskAuthor",
            Tier: "balanced",
            Kind: "llm",
            Files: "all",
            Commands: "all",
            SystemPrompt: SystemPrompt,
            OutputContract: """{ "markdown": string, "summary": string, "slug": string }""");

        var invocation = new StageInvocation(
            Stage: stageDef,
            Tier: "balanced",
            RunId: "fix-task-" + DateTimeOffset.UtcNow.Ticks,
            TargetRoot: rootPath,
            TaskName: taskId,
            TaskInput: prompt,
            LedgerSoFar: string.Empty,
            Manifest: [],
            LogSources: [],
            TraceDirectory: traceDir,
            ReportFile: reportFile,
            MaxTurns: config.MaxTurns,
            AbsoluteCeilingMs: config.SubagentTimeoutMilliseconds);

        SubagentResult result;
        try
        {
            result = await runner.RunAsync(invocation, ct);
        }
        catch (OperationCanceledException)
        {
            return new FixTaskAuthorOutcome(false, null, null, null, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new FixTaskAuthorOutcome(false, null, null, null, ex.Message);
        }

        if (!result.IsValid)
        {
            return new FixTaskAuthorOutcome(false, null, null, null,
                result.Error ?? "Subagent returned an invalid result.");
        }

        // Parse the JSON payload.
        try
        {
            var json = result.Json;
            if (string.IsNullOrWhiteSpace(json))
            {
                // Fall back to extracting from raw text.
                json = FencedJsonExtractor.Extract(result.RawText);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return new FixTaskAuthorOutcome(false, null, null, null,
                    "Subagent returned no parseable JSON.");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var markdown = root.TryGetProperty("markdown", out var md) ? md.GetString() : null;
            var summary = root.TryGetProperty("summary", out var s) ? s.GetString() : null;
            var slug = root.TryGetProperty("slug", out var sl) ? sl.GetString() : null;

            if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(slug))
            {
                return new FixTaskAuthorOutcome(false, null, null, null,
                    "Subagent returned JSON missing required fields (markdown and slug).");
            }

            return new FixTaskAuthorOutcome(true, markdown, summary, slug, null);
        }
        catch (JsonException ex)
        {
            return new FixTaskAuthorOutcome(false, null, null, null,
                $"Failed to parse subagent JSON: {ex.Message}");
        }
    }

    private static string BuildPrompt(string taskId, FailedRunContext ctx)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"Task \"{taskId}\" flagged during a Visual Relay run. Here are the failures:");
        sb.AppendLine();

        if (ctx.FlagReason is not null)
        {
            sb.AppendLine("## Flag reason");
            sb.AppendLine(ctx.FlagReason);
            sb.AppendLine();
        }

        if (ctx.FlaggedStage is { } stage)
        {
            sb.AppendLine($"## Flagged stage: {stage} ({ctx.FlaggedStageName ?? "unknown"})");
            if (ctx.FlaggedStageError is not null)
            {
                sb.AppendLine($"Error: {ctx.FlaggedStageError}");
            }

            sb.AppendLine();
        }

        if (ctx.VerifyOutputs.Count > 0)
        {
            sb.AppendLine("## Verify gate failures");
            foreach (var vo in ctx.VerifyOutputs)
            {
                sb.AppendLine($"### Stage {vo.Stage}, attempt {vo.Attempt}");
                sb.AppendLine(vo.Summary);
                sb.AppendLine();
            }
        }

        if (ctx.LedgerSummary is not null)
        {
            sb.AppendLine("## Ledger summaries (agent diagnoses)");
            sb.AppendLine(ctx.LedgerSummary);
            sb.AppendLine();
        }

        sb.AppendLine("Author a new llm-task markdown that will make these failures stop recurring.");
        sb.AppendLine("Make flaky / non-deterministic tests deterministic; fix root causes;");
        sb.AppendLine("address the enabler where possible. Never weaken, skip, or delete tests.");
        sb.AppendLine();
        sb.AppendLine("Output ONLY the JSON object as described in the system prompt.");

        return sb.ToString();
    }
}
