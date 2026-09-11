using System.Text.Json;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>One hunk the audit read as changing behavior rather than asserting it.</summary>
/// <param name="File">The file the hunk is in, as the diff spells it.</param>
/// <param name="Reason">What the model says the hunk changes.</param>
public sealed record AuthorTestAuditHunk(string File, string Reason);

/// <summary>What one audit call established.</summary>
/// <param name="ImplementationHunks">The reported hunks; empty when the diff only asserts.</param>
/// <param name="Error">Why the call established nothing, or null when it answered.</param>
public sealed record AuthorTestAuditResult(
    IReadOnlyList<AuthorTestAuditHunk> ImplementationHunks, string? Error);

/// <summary>
/// The optional second opinion on what the Author-tests stage wrote. The path
/// gate can only ask where a change sits; this asks what it does, which is the
/// one question a repository whose unit tests live inside the implementation
/// file cannot answer any other way.
/// <para>
/// It is advisory by construction: a reported hunk buys the stage the same
/// single re-ask the gate buys it, and nothing else. The audit never flags a
/// task, never records a check, and a call that fails is logged and forgotten.
/// </para>
/// </summary>
public static partial class AuthorTestDiffAuditor
{
    /// <summary>The event name every audit leaves in the run log.</summary>
    private const string EventName = "author_test_audit";

    /// <summary>The re-ask reason when the audit, rather than the gate, asked for it.</summary>
    public const string ReaskReason = "implementation hunks in the test diff";

    /// <summary>The synthetic stage the one call runs as.</summary>
    internal const string StageName = "Author-test-audit";

    /// <summary>How much of the diff the model is shown before it is cut.</summary>
    private const int DiffCharacterCap = 60_000;

    private static readonly string TruncationMarker =
        $"\n[diff truncated at {DiffCharacterCap} characters]\n";

    private const string Tier = "cheap";

    private const string SystemPrompt =
        "You audit one diff and report only the parts of it that change what a program does. "
        + "You never edit files and you never run commands. Answer with the JSON object alone.";

    /// <summary>
    /// Whether the audit applies at all: never when it is off, on every declared
    /// list when it is always, and otherwise only where the path gate could not
    /// decide on its own. An unrecognized mode is treated as the default the
    /// config loader falls back to.
    /// </summary>
    /// <param name="diffAudit">The configured <c>authorTests.diffAudit</c> mode.</param>
    /// <param name="verdicts">How the stage's declared test files were classified.</param>
    /// <returns>True when the diff is worth one model call.</returns>
    public static bool ShouldRun(string diffAudit, IReadOnlyList<AuthorTestScopeVerdict> verdicts)
    {
        if (string.Equals(diffAudit, AuthorTestsConfig.DiffAuditOff, StringComparison.Ordinal))
            return false;
        if (verdicts.Count == 0)
            return false;
        return string.Equals(diffAudit, AuthorTestsConfig.DiffAuditAlways, StringComparison.Ordinal)
            || verdicts.Any(verdict => verdict.Kind is not AuthorTestScopeKind.Separate);
    }

    /// <summary>
    /// What the one call is asked. The question is put in terms of what a hunk
    /// does — assert behavior or change it — so it holds in any repository: no
    /// language, framework, file convention or command is named anywhere in it.
    /// </summary>
    /// <param name="diff">The diff to audit.</param>
    /// <returns>The task input for the call.</returns>
    public static string BuildPrompt(string diff) =>
        $$"""
        The Author-tests stage of an automated change was allowed to add tests and
        nothing else. Below is everything it changed.

        Read it hunk by hunk and decide what each hunk does:
        - It ASSERTS behavior: a test case, its fixtures, its setup or its teardown,
          the values it expects, or a helper that exists only so tests can call it.
        - It CHANGES behavior: an edit to the logic, types, constants, defaults or
          data the program uses when it runs, whether or not the hunk sits in a file
          the stage called a test file.

        Report only the hunks that change behavior. Some conventions keep unit tests
        in the same file as the code under test, so where a hunk sits proves nothing
        on its own: judge the lines. Adding a case, renaming a test or tightening an
        expectation is never a behavior change.

        Answer with a single JSON object and nothing else:
        {"implementationHunks":[{"file":"<path as the diff spells it>","reason":"<what it changes, one sentence>"}]}
        Use an empty array when every hunk only asserts behavior.

        ## Diff

        {{diff}}
        """;

    /// <summary>
    /// Runs the one cheap-tier call over the stage's diff and publishes what it
    /// answered. Every failure — an unreachable model, an unreadable answer, a
    /// git command that did not run — comes back as an <c>Error</c> with no
    /// hunks, so the caller can carry on exactly as it would have.
    /// </summary>
    /// <param name="rootPath">The workspace root.</param>
    /// <param name="taskId">The task being run.</param>
    /// <param name="runId">The run the call belongs to.</param>
    /// <param name="testFiles">What the stage declared as its test files.</param>
    /// <param name="config">The repository config, for the mode and the turn budget.</param>
    /// <param name="runner">The subagent runner the call goes through.</param>
    /// <param name="git">The git invoker the diff is read with.</param>
    /// <param name="sink">Where the <c>author_test_audit</c> event is published.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The audit's answer.</returns>
    public static async Task<AuthorTestAuditResult> RunAsync(
        string rootPath,
        string taskId,
        string runId,
        IReadOnlyList<string> testFiles,
        RelayConfig config,
        ISubagentRunner runner,
        IGitInvoker git,
        IRelayEventSink sink,
        CancellationToken cancellationToken)
    {
        AuthorTestAuditResult result;
        try
        {
            var diff = await BuildDiffAsync(rootPath, testFiles, git, cancellationToken);
            // The call's report lands beside the stage's own attempts, where the
            // stage's cumulative pricing picks it up; nothing is carried back.
            var answer = await runner.RunAsync(
                Invocation(rootPath, taskId, runId, BuildPrompt(diff), config), cancellationToken);
            result = Read(answer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new AuthorTestAuditResult([], ex.Message);
        }

        await sink.PublishAsync(
            new RelayEvent(DateTimeOffset.UtcNow, "info", EventName, runId, rootPath, taskId,
                StageNumber: 5, Tier: Tier, Data: EventData(config.AuthorTests.DiffAudit, result)),
            cancellationToken);
        return result;
    }

    private static StageInvocation Invocation(
        string rootPath, string taskId, string runId, string prompt, RelayConfig config)
    {
        // One directory per call, as every stage attempt gets: the stage can audit
        // twice (once per gate pass), and a shared directory would merge the two
        // trace sessions and leave the first call's report standing as the second's.
        var traceDirectory = TraceDirectory(rootPath, taskId, CallCount(rootPath, taskId) + 1);
        Directory.CreateDirectory(traceDirectory);
        var stage = new RelayStageDefinition(
            Number: 0,
            Name: StageName,
            Tier: Tier,
            Kind: "llm",
            Files: "none",
            Commands: "none",
            SystemPrompt: SystemPrompt,
            // Only the object's own key is named. The contract reader requires
            // every key the line spells, so naming the element's keys here made
            // an empty answer — the ordinary one — impossible to satisfy.
            OutputContract: "End your reply with a single fenced ```json block, nothing after it, "
                + """matching: { "implementationHunks": [ {file, reason} ] }""");

        return new StageInvocation(
            stage, Tier, runId, rootPath, taskId, prompt,
            LedgerSoFar: string.Empty,
            Manifest: [],
            LogSources: [],
            TraceDirectory: traceDirectory,
            ReportFile: traceDirectory + ".report.json",
            MaxTurns: 1,
            AbsoluteCeilingMs: config.SubagentTimeoutMilliseconds,
            // The whole diff is in the prompt, so there is nothing to look up and
            // one turn is enough. Offered a tool catalog, the model spent that one
            // turn on two lookups and the audit reported no answer at all.
            WithoutTools: true);
    }

    private static string TraceDirectory(string rootPath, string taskId, int call) =>
        Path.Combine(rootPath, ".relay", taskId, $"stage5-audit{call}");

    /// <summary>How many audit calls this task has already made.</summary>
    private static int CallCount(string rootPath, string taskId)
    {
        var taskDirectory = Path.Combine(rootPath, ".relay", taskId);
        return Directory.Exists(taskDirectory)
            ? Directory.EnumerateDirectories(taskDirectory, "stage5-audit*").Count()
            : 0;
    }

    private static AuthorTestAuditResult Read(SubagentResult answer)
    {
        if (!answer.IsValid)
            return new AuthorTestAuditResult([], answer.Error ?? "the audit call returned no result");

        var json = string.IsNullOrWhiteSpace(answer.Json)
            ? FencedJsonExtractor.Extract(answer.RawText)
            : answer.Json;
        if (string.IsNullOrWhiteSpace(json))
            return new AuthorTestAuditResult([], "the audit answered with no readable JSON");

        try
        {
            using var document = JsonDocument.Parse(json);
            // An object without the array is not a verdict: the model answered
            // something, but not the question, so nothing was established.
            if (!document.RootElement.TryGetProperty("implementationHunks", out var hunks)
                || hunks.ValueKind != JsonValueKind.Array)
                return new AuthorTestAuditResult([], "the audit answered without an implementationHunks array");

            return new AuthorTestAuditResult([.. hunks.EnumerateArray().Select(Hunk).OfType<AuthorTestAuditHunk>()], null);
        }
        catch (JsonException ex)
        {
            return new AuthorTestAuditResult([], $"the audit answered with unparseable JSON: {ex.Message}");
        }
    }

    private static AuthorTestAuditHunk? Hunk(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        var file = element.TryGetProperty("file", out var f) ? f.GetString() : null;
        var reason = element.TryGetProperty("reason", out var r) ? r.GetString() : null;
        return string.IsNullOrWhiteSpace(file) ? null : new AuthorTestAuditHunk(file, reason ?? string.Empty);
    }

    private static Dictionary<string, string> EventData(string mode, AuthorTestAuditResult result)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal) { ["mode"] = mode };
        if (result.Error is not null)
        {
            data["error"] = result.Error;
            return data;
        }

        var reasons = string.Join("; ", result.ImplementationHunks.Select(hunk => hunk.Reason));
        data["hunks"] = result.ImplementationHunks.Count.ToString();
        data["files"] = string.Join(',', result.ImplementationHunks.Select(hunk => hunk.File));
        data["reasons"] = reasons.Length > 240 ? reasons[..240] : reasons;
        return data;
    }
}
