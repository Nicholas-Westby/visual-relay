using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Part B: richer verify output handed to the Verify/Fix-verify agent —
/// (1) the kept tail is at least tripled (>= 1800 chars) so the pass/fail summary
/// and real errors survive trailing noise, and (2) the prompt names the persisted
/// full-output file path so the agent can read the complete log.
/// </summary>
public sealed class VerifyOutputTailAndPathTests
{
    // ── (1) tail window ──────────────────────────────────────────────────

    [Fact]
    public void TrimForTail_DefaultWindow_KeepsAtLeast1800Chars()
    {
        // A 5000-char body must keep a tail of >= 1800 chars (was ~600). The leading
        // "…" marks truncation and is not part of the kept window.
        var body = new string('x', 5000);

        var trimmed = SwivalSubagentRunner.TrimForTail(body);

        var keptLength = trimmed.StartsWith('…') ? trimmed.Length - 1 : trimmed.Length;
        Assert.True(keptLength >= 1800, $"expected >= 1800 kept chars, got {keptLength}");
    }

    [Fact]
    public void TrimForTail_KeepsTheEnd_NotTheHead()
    {
        // The real error sits at the END after the sandbox banner — keep the tail.
        var body = new string('A', 5000) + "REAL_ERROR_AT_END";

        var trimmed = SwivalSubagentRunner.TrimForTail(body);

        Assert.EndsWith("REAL_ERROR_AT_END", trimmed);
        Assert.StartsWith("…", trimmed);
    }

    // ── (2) full-output path in the prompt ───────────────────────────────

    [Fact]
    public void BuildPrompt_WithVerifyOutputPath_NamesTheFullOutputFile()
    {
        const string path = "/repo/.relay/task/stage10-attempt2.verify-output.txt";
        var invocation = MakeVerifyInvocation(lastTestOutput: "Failed! - 3 failed", verifyOutputPath: path);

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.Contains("## Verify output", prompt, StringComparison.Ordinal);
        Assert.Contains(path, prompt, StringComparison.Ordinal);
        Assert.Contains("read it for the complete log", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_WithoutVerifyOutputPath_OmitsFullOutputLine()
    {
        var invocation = MakeVerifyInvocation(lastTestOutput: "Failed! - 3 failed", verifyOutputPath: null);

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.Contains("## Verify output", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Full output:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_LongVerifyOutput_KeepsTripledTail()
    {
        // The ## Verify output section must carry the enlarged tail, not the old ~600.
        var body = new string('A', 5000) + "PASS/FAIL SUMMARY AT END";
        var invocation = MakeVerifyInvocation(lastTestOutput: body, verifyOutputPath: null);

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.Contains("PASS/FAIL SUMMARY AT END", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Count(c => c == 'A') >= 1800,
            "the verify-output tail in the prompt must keep >= 1800 chars of the body");
    }

    // ── (3) persisted full-output path is absolute ───────────────────────

    [Fact]
    public void TryPersistVerifyOutput_RelativeTaskDirectory_ReturnsAbsolutePath()
    {
        // Fix #2: the path handed to the agent (read under the sandbox's --allow-cwd grant)
        // must be absolute regardless of how the root was passed. A RELATIVE task directory
        // must still yield a fully-qualified path — a relative one would break the read.
        var absoluteDir = Path.Combine(Path.GetTempPath(), "vr-verify-abspath", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(absoluteDir);
        try
        {
            var relativeDir = Path.GetRelativePath(Directory.GetCurrentDirectory(), absoluteDir);
            Assert.False(Path.IsPathFullyQualified(relativeDir),
                "precondition: the task directory under test must be relative");

            var path = RelayDriver.TryPersistVerifyOutput(relativeDir, 9, 1, "red", "some output");

            Assert.NotNull(path);
            Assert.True(Path.IsPathFullyQualified(path!), $"expected an absolute path, got: {path}");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(absoluteDir);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static StageInvocation MakeVerifyInvocation(string? lastTestOutput, string? verifyOutputPath) =>
        new(
            Stage: RelayStages.All[9], // Stage 10 — Fix-verify
            Tier: "balanced",
            RunId: "run-1",
            TargetRoot: "/repo",
            TaskName: "task",
            TaskInput: "# task",
            LedgerSoFar: string.Empty,
            Manifest: ["src/app.cs"],
            LogSources: [],
            TraceDirectory: "/tmp/trace",
            ReportFile: "/tmp/report.json",
            MaxTurns: 200,
            LastTestOutput: lastTestOutput,
            VerifyOutputPath: verifyOutputPath);
}
