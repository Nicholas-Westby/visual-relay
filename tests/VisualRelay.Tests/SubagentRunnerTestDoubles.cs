using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

internal sealed class ScriptedSubagentRunner : ISubagentRunner
{
    private string _codeFile = "src/app.cs";
    private string _testFile = "tests/app.tests.cs";
    private bool _nonCodeOnly;
    private string _nonCodeFile = "docs/README.md";
    private bool _codeOnly;
    private string _codeOnlyFile = "src/View.axaml";
    private bool _testOnly;
    private string _testOnlyFile = "tests/regression.cs";

    public void SeedHappyPath(string codeFile, string testFile)
    {
        _codeFile = codeFile;
        _testFile = testFile;
    }

    // A non-code change: the manifest contains only documentation/config files
    // (e.g. .md, .txt, .json). Stage 5 returns no testFiles.
    public void SeedNonCodeOnly(string nonCodeFile)
    {
        _nonCodeOnly = true;
        _nonCodeFile = nonCodeFile;
    }

    // A code-only change: the manifest contains only implementation code files
    // (e.g. .axaml, .ts, .py) with no authored tests. Stage 5 returns no testFiles.
    public void SeedCodeOnly(string codeFile)
    {
        _codeOnly = true;
        _codeOnlyFile = codeFile;
    }

    // A test-only change: the manifest contains only test files (already covered
    // by existing tests). Stage 5 returns the test file as a testFile.
    public void SeedTestOnly(string testFile)
    {
        _testOnly = true;
        _testOnlyFile = testFile;
    }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 when _nonCodeOnly => $$"""{"plan":"edit docs","manifest":["{{_nonCodeFile}}"]}""",
            4 when _codeOnly => $$"""{"plan":"edit code","manifest":["{{_codeOnlyFile}}"]}""",
            4 when _testOnly => $$"""{"plan":"add tests","manifest":["{{_testOnlyFile}}"]}""",
            4 => $$"""{"plan":"edit files","manifest":["{{_codeFile}}","{{_testFile}}"]}""",
            5 when _nonCodeOnly => """{"testFiles":[],"rationale":"documentation-only; nothing to unit-test"}""",
            5 when _codeOnly => """{"testFiles":[],"rationale":"code change without authored tests"}""",
            5 when _testOnly => $$"""{"testFiles":["{{_testOnlyFile}}"],"rationale":"test-only change"}""",
            5 => $$"""{"testFiles":["{{_testFile}}"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed review notes"}""",
            9 => """{"summary":"verified","commitMessages":["feat: implement feature","fix: address edge case","chore: update project files"]}""",
            10 => """{"summary":"fixed verify"}""",
            _ => """{"summary":"ok"}"""
        };

        return Task.FromResult(new SubagentResult(
            RawText: $"```json{Environment.NewLine}{json}{Environment.NewLine}```",
            Json: json,
            IsValid: true,
            Error: null));
    }
}

/// <summary>
/// Wraps a <see cref="ScriptedSubagentRunner"/> and records every
/// <see cref="StageInvocation"/> passed to <see cref="RunAsync"/> so tests
/// can assert on prompt data (e.g. <see cref="StageInvocation.LastTestOutput"/>,
/// <see cref="StageInvocation.TestCommand"/>) that the canned runner ignores.
/// </summary>
internal sealed class CapturingSubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private readonly List<StageInvocation> _invocations = [];

    public IReadOnlyList<StageInvocation> Invocations => _invocations;

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        _invocations.Add(invocation);
        return _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// Returns <see cref="SubagentResult"/> with <see cref="SubagentResult.IsValid"/> = true
/// and <see cref="SubagentResult.Json"/> set to a JSON array (<c>[1,2,3]</c>) for every
/// stage — simulating a bug where non-object JSON reaches the driver. Used to verify the
/// driver's defensive shape validation flags cleanly instead of throwing.
/// </summary>
internal sealed class ArrayRootSubagentRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SubagentResult("[1,2,3]", "[1,2,3]", true, null));
    }
}

/// <summary>
/// Wraps an inner <see cref="ISubagentRunner"/> (defaults to <see cref="ScriptedSubagentRunner"/>)
/// and returns an invalid result for stages at or after <paramref name="flagAtStage"/>,
/// simulating a flagged run that stops partway through the stage loop.
/// </summary>
internal sealed class FlagAtStageSubagentRunner : ISubagentRunner
{
    private readonly int _flagAtStage;
    private readonly ISubagentRunner _inner;

    public FlagAtStageSubagentRunner(int flagAtStage, ISubagentRunner? inner = null)
    {
        _flagAtStage = flagAtStage;
        _inner = inner ?? new ScriptedSubagentRunner();
    }

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number < _flagAtStage)
        {
            return await _inner.RunAsync(invocation, cancellationToken);
        }

        // Create the trace directory so RelayAttempt.Next sees this attempt
        // (matching real Swival behavior where trace dirs exist even for failures).
        Directory.CreateDirectory(invocation.TraceDirectory);
        return new SubagentResult(
            RawText: string.Empty,
            Json: null,
            IsValid: false,
            Error: $"synthetic flag at stage {_flagAtStage}");
    }
}

/// <summary>
/// Wraps a <see cref="ScriptedSubagentRunner"/> and returns stage-7 results
/// from a FIFO queue.  Non-stage-7 calls delegate to the inner runner.
/// Also captures every <see cref="StageInvocation"/> so tests can assert on
/// call count and <see cref="StageInvocation.Tier"/> values.
/// </summary>
internal sealed class Stage7SequenceRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private readonly Queue<string> _stage7Results = new();
    private readonly List<StageInvocation> _invocations = [];
    private readonly Dictionary<int, string> _stageOverrides = new();

    public IReadOnlyList<StageInvocation> Invocations => _invocations;

    /// <summary>
    /// Enqueue a stage-7 JSON result.  Dequeued in FIFO order on each
    /// stage-7 call.  When the queue is exhausted, falls back to the
    /// inner <see cref="ScriptedSubagentRunner"/> (which returns
    /// <c>{"verdict":"pass","issues":[]}</c>).
    /// </summary>
    public void EnqueueStage7Result(string json)
    {
        _stage7Results.Enqueue(json);
    }

    /// <summary>
    /// Override the JSON result for a specific stage number.
    /// Takes precedence over the inner scripted runner and the stage-7 queue.
    /// </summary>
    public void SetStageResult(int stageNumber, string json)
    {
        _stageOverrides[stageNumber] = json;
    }

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        _invocations.Add(invocation);

        if (invocation.Stage.Number == 7 && _stage7Results.Count > 0)
        {
            var json = _stage7Results.Dequeue();
            return Task.FromResult(new SubagentResult(
                RawText: $"```json{Environment.NewLine}{json}{Environment.NewLine}```",
                Json: json,
                IsValid: true,
                Error: null));
        }

        if (_stageOverrides.TryGetValue(invocation.Stage.Number, out var overrideJson))
        {
            return Task.FromResult(new SubagentResult(
                RawText: $"```json{Environment.NewLine}{overrideJson}{Environment.NewLine}```",
                Json: overrideJson,
                IsValid: true,
                Error: null));
        }

        return _inner.RunAsync(invocation, cancellationToken);
    }
}
