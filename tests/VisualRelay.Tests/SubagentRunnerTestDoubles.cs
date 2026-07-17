using VisualRelay.Core.Execution;
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
    private bool _reviewChanges;

    public void SeedHappyPath(string codeFile, string testFile)
    {
        _codeFile = codeFile;
        _testFile = testFile;
    }

    // Makes Review (stage 7) return a non-clean verdict so the driver's
    // clean-review Fix-skip does NOT trigger and Fix (stage 9) runs — for tests
    // that exercise Fix's mechanics (invocation, targeted command, flag/resume).
    public void SeedReviewChanges() => _reviewChanges = true;

    public void SeedNonCodeOnly(string nonCodeFile) { _nonCodeOnly = true; _nonCodeFile = nonCodeFile; }
    public void SeedCodeOnly(string codeFile) { _codeOnly = true; _codeOnlyFile = codeFile; }
    public void SeedTestOnly(string testFile) { _testOnly = true; _testOnlyFile = testFile; }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var json = invocation.Stage.Number switch
        {
            0 => """{"visualReview":"needed","reason":"change includes UI files"}""",
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
            7 => _reviewChanges
                ? """{"verdict":"changes","issues":["address the review finding"]}"""
                : """{"verdict":"pass","issues":[]}""",
            8 => """{"verdict":"pass","issues":[]}""",
            9 => """{"summary":"fixed review notes"}""",
            10 => """{"summary":"verified","commitMessages":["feat: implement feature","fix: address edge case","chore: update project files"]}""",
            11 => """{"summary":"fixed verify"}""",
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

    public void SeedReviewChanges() => _inner.SeedReviewChanges();

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
/// Decorates an inner runner so that at <paramref name="stage"/> it writes
/// <paramref name="content"/> to <paramref name="relativePath"/> under the target
/// root. Gives an otherwise file-less scripted runner a real working-tree change,
/// so a code-expecting run actually produces code (as a real agent would) and
/// clears the completion gate.
/// </summary>
internal sealed class FileWritingSubagentRunner(
    ISubagentRunner inner, int stage, string relativePath, string content) : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == stage)
        {
            var full = Path.Combine(invocation.TargetRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// Wraps an inner <see cref="ISubagentRunner"/> (defaults to <see cref="ScriptedSubagentRunner"/>)
/// and returns an invalid result for stages at or after <c>flagAtStage</c>,
/// simulating a flagged run that stops partway through the stage loop.
/// </summary>
internal sealed class FlagAtStageSubagentRunner(int flagAtStage, ISubagentRunner? inner = null) : ISubagentRunner
{
    private readonly ISubagentRunner _inner = inner ?? new ScriptedSubagentRunner();

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number < flagAtStage)
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
            Error: $"synthetic flag at stage {flagAtStage}");
    }
}

/// <summary>
/// Returns <c>{"visualReview":"skip","reason":"no visual changes"}</c> for
/// the triage stage (0) so the visual-review is skipped.
/// </summary>
internal sealed class TriageSkipSubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 0)
        {
            return Task.FromResult(new SubagentResult(
                RawText: """```json\n{"visualReview":"skip","reason":"no visual changes"}\n```""",
                Json: """{"visualReview":"skip","reason":"no visual changes"}""",
                IsValid: true,
                Error: null));
        }
        return _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// Returns a triage verdict other than <c>"needed"</c>/<c>"skip"</c> for the
/// triage stage (0), so Visual-review is skipped via the fallback reason branch
/// (<c>_Skipped: vision tier unconfigured_</c>) rather than the triage-skip one.
/// </summary>
internal sealed class TriageDeclineSubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 0)
        {
            return Task.FromResult(new SubagentResult(
                RawText: """```json\n{"visualReview":"none","reason":"no rendered surface"}\n```""",
                Json: """{"visualReview":"none","reason":"no rendered surface"}""",
                IsValid: true,
                Error: null));
        }
        return _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// Wraps a <see cref="ScriptedSubagentRunner"/> and returns an invalid result
/// for the specified <paramref name="flagAtStage"/>, simulating a stage that
/// produces no valid JSON.
/// </summary>
internal sealed class FlagStageSubagentRunner(int flagAtStage) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == flagAtStage)
        {
            Directory.CreateDirectory(invocation.TraceDirectory);
            return new SubagentResult(RawText: string.Empty, Json: null, IsValid: false, Error: "invalid result");
        }
        return await _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// Returns a watchdog-kill <see cref="SubagentResult"/> for the specified stage,
/// simulating an agent process killed by the activity watchdog. All other stages
/// delegate to the inner <see cref="ScriptedSubagentRunner"/>.
/// </summary>
internal sealed class KillSubagentRunner(int killAtStage) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == killAtStage)
        {
            Directory.CreateDirectory(invocation.TraceDirectory);
            var traceDirParent = Path.GetDirectoryName(invocation.TraceDirectory)!;
            var autopsyPath = Path.Combine(traceDirParent, $"stage{killAtStage}-attempt1.killed-output.txt");
            return Task.FromResult(new SubagentResult(
                RawText: string.Empty, Json: null, IsValid: false,
                Error: "swival timed out after 55m 00s absolute ceiling. Last signal: cpu, silence: 52373ms.",
                HardAbort: true,
                Kill: new KillSignature("absolute_ceiling", "cpu", 52373, autopsyPath)));
        }
        return _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// Returns a watchdog-kill result on the FIRST call to the specified stage, then
/// delegates to the inner scripted runner on subsequent calls — simulating a
/// retry that succeeds after an infrastructure kill.
/// </summary>
internal sealed class TwoPhaseKillSubagentRunner(int killAtStage) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private int _callCount;

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == killAtStage)
        {
            Directory.CreateDirectory(invocation.TraceDirectory);
            var count = Interlocked.Increment(ref _callCount);
            if (count == 1)
            {
                var traceDirParent = Path.GetDirectoryName(invocation.TraceDirectory)!;
                var autopsyPath = Path.Combine(traceDirParent, $"stage{killAtStage}-attempt1.killed-output.txt");
                return new SubagentResult(
                    RawText: string.Empty, Json: null, IsValid: false,
                    Error: "swival timed out after 55m 00s absolute ceiling. Last signal: cpu, silence: 52373ms.",
                    HardAbort: true,
                    Kill: new KillSignature("absolute_ceiling", "cpu", 52373, autopsyPath));
            }
        }
        return await _inner.RunAsync(invocation, cancellationToken);
    }
}
