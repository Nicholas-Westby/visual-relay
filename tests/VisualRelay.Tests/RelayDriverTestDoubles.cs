using System.Text.Json;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Drives a whole run whose Plan manifest and Author-tests list the test fixes,
/// writing the files <paramref name="stage5Writes"/> names while stage 5 runs so
/// the author-test gate has a real working tree to judge. Every stage-5 task
/// input is recorded, so a re-ask (a second stage-5 call carrying an extra
/// instruction) is visible to the test.
/// </summary>
internal sealed class AuthorTestStageRunner(
    IReadOnlyList<string> manifest,
    IReadOnlyList<string> testFiles,
    IReadOnlyDictionary<string, string>? stage5Writes = null) : ISubagentRunner
{
    private readonly List<string> _stage5Inputs = [];
    private readonly List<StageInvocation> _auditCalls = [];

    /// <summary>The task input of each stage-5 call, in order.</summary>
    public IReadOnlyList<string> Stage5Inputs => _stage5Inputs;

    /// <summary>Every diff-audit call, in order; empty when the audit never ran.</summary>
    public IReadOnlyList<StageInvocation> AuditCalls => _auditCalls;

    /// <summary>The audit's answer; null answers with no implementation hunks.</summary>
    public string? AuditAnswer { get; init; }

    /// <summary>When true the audit's call comes back invalid, as a failed call does.</summary>
    public bool AuditFails { get; set; }

    /// <summary>When true every call leaves the stage report a real runner leaves.</summary>
    public bool CostReports { get; init; }

    /// <summary>When true the re-asked stage (the second stage-5 call) answers unusably.</summary>
    public bool ReaskFails { get; init; }

    /// <summary>What that unusable re-ask writes to the tree before it answers.</summary>
    public IReadOnlyDictionary<string, string>? ReaskWrites { get; init; }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (CostReports)
            StageReportSeed.Write(invocation);

        if (invocation.Stage.Name == AuthorTestDiffAuditor.StageName)
        {
            _auditCalls.Add(invocation);
            var answer = AuditAnswer ?? """{"implementationHunks":[]}""";
            return Task.FromResult(AuditFails
                ? new SubagentResult(string.Empty, null, false, "audit call failed")
                : new SubagentResult(answer, answer, true, null));
        }

        if (invocation.Stage.Number == 5)
        {
            _stage5Inputs.Add(invocation.TaskInput);
            if (ReaskFails && _stage5Inputs.Count > 1)
            {
                WriteAll(invocation.TargetRoot, ReaskWrites);
                return Task.FromResult(new SubagentResult(string.Empty, null, false, "re-ask failed"));
            }

            WriteAll(invocation.TargetRoot, stage5Writes);
        }

        var json = invocation.Stage.Number switch
        {
            0 => """{"visualReview":"skip","reason":"no visual changes"}""",
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => $$"""{"plan":"edit files","manifest":{{JsonSerializer.Serialize(manifest)}}}""",
            5 => $$"""{"testFiles":{{JsonSerializer.Serialize(testFiles)}},"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"verdict":"pass","issues":[]}""",
            9 => """{"summary":"fixed"}""",
            10 => """{"summary":"verified","commitMessages":["feat: add the behavior"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }

    private static void WriteAll(string root, IReadOnlyDictionary<string, string>? writes)
    {
        foreach (var (relative, content) in writes ?? new Dictionary<string, string>())
        {
            var full = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }
}

internal sealed class PrematureImplementationRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        // Stage 4: premature implementation — written early, then reverted
        // by the WorktreeFilter at stage 5 so the red-gate sees a clean
        // production file and tests fail red.
        if (invocation.Stage.Number == 4)
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.cs"), "new\n");

        // Stage 5: author the test file on disk. The WorktreeFilter keeps
        // this (it's in testFiles) and reverts the stage-4 production edit.
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "status.test"), "expects new status");
        }

        // Stage 6: the real implementation — after WorktreeFilter at stage 5
        // reverted the production file to HEAD, the agent re-implements it.
        if (invocation.Stage.Number == 6)
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.cs"), "new\n");

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit status","manifest":["src/status.cs","tests/status.test","src/ghost.cs"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implement status.cs"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"no changes"}""",
            9 => """{"summary":"verified"}""",
            10 => """{"summary":"no changes"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

internal sealed class ArtifactWritingSubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _scripted = new();
    public void SeedHappyPath(string codeFile, string testFile) => _scripted.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(invocation.TraceDirectory);
        await File.WriteAllTextAsync(Path.Combine(invocation.TraceDirectory, $"{Guid.NewGuid():N}.jsonl"),
            """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""", cancellationToken);
        await File.WriteAllTextAsync(invocation.ReportFile,
            """{ "model": "cheap", "result": { "outcome": "success" }, "stats": {}, "timeline": [] }""", cancellationToken);
        return await _scripted.RunAsync(invocation, cancellationToken);
    }
}

internal sealed class ThrowingSubagentRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("kaboom");
}

internal sealed class RedGateObservingTestRunner : ITestRunner
{
    private readonly string _rootPath;
    // ReSharper disable once ConvertToPrimaryConstructor — a primary-ctor 'rootPath'
    // would be shadowed by RunAsync(string rootPath); the field disambiguates the
    // captured construction-time root from the per-call argument.
    public RedGateObservingTestRunner(string rootPath) => _rootPath = rootPath;
    public List<string> StatusSnapshots { get; } = [];

    public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        // The gate runs either in-place at the live root (stage-5 author gate) or,
        // for the authoritative verify gates (stage 9 / stage 10), in a Task-8
        // full-fidelity isolated snapshot under the visual-relay worktree temp
        // namespace. Either way the snapshot mirrors the live tree, so the status
        // content read here is identical.
        Assert.True(
            string.Equals(_rootPath, rootPath, StringComparison.Ordinal) || IsVerifySnapshot(rootPath),
            $"unexpected gate rootPath: {rootPath}");
        var status = File.ReadAllText(Path.Combine(rootPath, "src", "status.cs")).Trim();
        StatusSnapshots.Add(status);
        return Task.FromResult(command == "full-suite"
            ? new TestRunResult(status == "new" ? 0 : 1, status)
            : new TestRunResult(status == "old" ? 1 : 0, status));
    }

    private static bool IsVerifySnapshot(string path) =>
        path.Replace('\\', '/').Contains("/visual-relay/wt/", StringComparison.Ordinal);
}

internal sealed class BadManifestSubagentRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit files","manifest":["llm-tasks/extra.md","src/real.cs"]}""",
            5 => """{"testFiles":["tests/real.tests.cs"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"no changes"}""",
            9 => """{"summary":"verified","commitMessages":["feat: add real feature"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

internal sealed class OnlyTaskDirManifestSubagentRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"bookkeeping","manifest":["llm-tasks/a.md","llm-tasks/b.md"]}""",
            5 => """{"testFiles":[],"rationale":"no code changes"}""",
            6 => """{"summary":"nothing to implement"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"no changes needed"}""",
            9 => """{"summary":"verified","commitMessages":["chore: bookkeeping"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}
/// <summary>
/// Simulates an agent that front-loads implementation at <b>stage 3</b> (Diagnose)
/// by writing the impl file(s) from the manifest directly to disk before the Plan
/// stage has even produced the manifest. This is the scenario the down-shift
/// feature targets: the implementation is already in the working tree when
/// Implement (stage 6) is about to run.
/// </summary>
internal sealed class Stage3FrontLoadRunner : ISubagentRunner
{
    private bool _frontLoaded;

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        // Front-load at stage 3: write the canonical impl file before Plan runs.
        if (invocation.Stage.Number == 3 && !_frontLoaded)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "src"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.cs"), "new\n");
            _frontLoaded = true;
        }

        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "status.test"), "expects new status");
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit status","manifest":["src/status.cs","tests/status.test"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implementation already present"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"no changes"}""",
            9 => """{"summary":"verified"}""",
            10 => """{"summary":"no changes"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

internal sealed class TurnsReportingSubagentRunner(int llmCallCount) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _scripted = new();

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(invocation.TraceDirectory);
        await File.WriteAllTextAsync(Path.Combine(invocation.TraceDirectory, $"{Guid.NewGuid():N}.jsonl"),
            """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""", cancellationToken);
        await File.WriteAllTextAsync(invocation.ReportFile,
            $$"""{"model":"cheap","result":{"answer":"ok"},"stats":{},"timeline":[{{string.Join(",", Enumerable.Range(0, llmCallCount).Select(i => $$"""{"type":"llm_call","prompt_tokens_est":{{(i + 1) * 1000}}}"""))}}]}""", cancellationToken);
        return await _scripted.RunAsync(invocation, cancellationToken);
    }
}
