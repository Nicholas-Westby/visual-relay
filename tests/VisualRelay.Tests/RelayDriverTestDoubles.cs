using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

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
    public RedGateObservingTestRunner(string rootPath) => _rootPath = rootPath;
    public List<string> StatusSnapshots { get; } = [];

    public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        Assert.Equal(_rootPath, rootPath);
        var status = File.ReadAllText(Path.Combine(rootPath, "src", "status.cs")).Trim();
        StatusSnapshots.Add(status);
        return Task.FromResult(command == "full-suite"
            ? new TestRunResult(status == "new" ? 0 : 1, status)
            : new TestRunResult(status == "old" ? 1 : 0, status));
    }
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

internal sealed class TurnsReportingSubagentRunner : ISubagentRunner
{
    private readonly int _llmCallCount;
    private readonly ScriptedSubagentRunner _scripted = new();
    public TurnsReportingSubagentRunner(int llmCallCount) => _llmCallCount = llmCallCount;

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(invocation.TraceDirectory);
        await File.WriteAllTextAsync(Path.Combine(invocation.TraceDirectory, $"{Guid.NewGuid():N}.jsonl"),
            """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""", cancellationToken);
        await File.WriteAllTextAsync(invocation.ReportFile,
            $$"""{"model":"cheap","result":{"answer":"ok"},"stats":{},"timeline":[{{string.Join(",", Enumerable.Range(0, _llmCallCount).Select(i => $$"""{"type":"llm_call","prompt_tokens_est":{{(i + 1) * 1000}}}"""))}}]}""", cancellationToken);
        return await _scripted.RunAsync(invocation, cancellationToken);
    }
}
