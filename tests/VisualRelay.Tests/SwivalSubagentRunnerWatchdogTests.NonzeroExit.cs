using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Nonzero-exit retry-and-persist tests. When a swival process exits nonzero
// without producing a valid report, the runner must persist the full captured
// output and retry within the shared MaxStallRetries budget — exactly like the
// stall path does. These tests assert the target behavior; they will fail
// against the current fail-fast nonzero-exit branch.
public sealed partial class SwivalSubagentRunnerWatchdogTests
{
    /// <summary>
    /// A swival that exits 1 on the first attempt (with diagnostic stderr) and
    /// succeeds on the second attempt must be retried: the first attempt's
    /// output is persisted to killed-output.txt, and the final result is valid.
    /// </summary>
    [Fact]
    public async Task RunAsync_NonzeroExitThenRecover_RetriesAndReturnsSuccess()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-exit1-then-recover",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            if [[ "$trace_dir" == *attempt2* ]]; then
              mkdir -p "$trace_dir"
              printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"recovered after crash"}]}}' > "$trace_dir/trace.jsonl"
              printf '```json\n{"summary":"recovered","options":["small"]}\n```\n'
              exit 0
            else
              echo "non-zero exit simulation banner: some sandbox header" >&2
              echo "FATAL: malformed JSON arguments in tool call at line 42" >&2
              exit 1
            fi
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 30_000,
            MaxStallRetries = 1
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        // The retry succeeded — the final result must be valid.
        Assert.True(result.IsValid, $"expected retry to succeed, got error: {result.Error}");
        Assert.Null(result.Error);
        Assert.Contains("recovered", result.Json, StringComparison.Ordinal);

        // The first (crashed) attempt's full output must be persisted.
        var persisted = Path.Combine(repo.Root, ".relay", "task", "stage1-attempt1.killed-output.txt");
        Assert.True(File.Exists(persisted), $"expected killed-output at {persisted}");
        var content = await File.ReadAllTextAsync(persisted);
        Assert.Contains("FATAL: malformed JSON arguments in tool call at line 42", content, StringComparison.Ordinal);
        Assert.Contains("sandbox header", content, StringComparison.Ordinal);

        // The second attempt must NOT leave a killed-output file (it succeeded).
        var secondPersisted = Path.Combine(repo.Root, ".relay", "task", "stage1-attempt2.killed-output.txt");
        Assert.False(File.Exists(secondPersisted), "successful retry should not leave a killed-output artifact");
    }

    /// <summary>
    /// A swival that always exits 1 with a long startup banner followed by
    /// the real error at the tail must, after retry exhaustion, produce a flag
    /// reason that contains the TAIL of the output (where the real error is)
    /// rather than only the head (which is just the sandbox banner). The
    /// persisted killed-output files must exist for every attempt.
    /// </summary>
    [Fact]
    public async Task RunAsync_AlwaysNonzeroExit_FlagsAfterMaxRetriesWithTailNotHead()
    {
        using var repo = TestRepository.Create();
        // Produce a realistic nono-style output: a long startup banner (the
        // first ~800 chars are just the sandbox header), then the real error.
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-always-exit1",
            """
            #!/usr/bin/env bash
            # Simulate nono startup banner — head content that a head-truncation
            # would capture, hiding the real error at the tail (see TrimForTail).
            echo "nono v0.62.0 — sandbox active" >&2
            echo "profile: vr-guard" >&2
            echo "allow-cwd: enabled" >&2
            echo "rollback: enabled" >&2
            echo "===============================================================================" >&2
            echo "" >&2
            echo "swival starting up..." >&2
            echo "loading model configuration..." >&2
            echo "initializing tool registry..." >&2
            echo "connecting to backend at http://127.0.0.1:4000" >&2
            echo "" >&2
            # Pad to ensure the head is mostly banner. The real error is below.
            printf '%*s\n' 400 '' | tr ' ' '=' >&2
            echo "" >&2
            echo "CRITICAL: litellm.BadRequestError: OpenAIException - /chat/completions:" >&2
            echo "Invalid model name passed in model=balanced" >&2
            echo "Available models: gpt-4o, claude-sonnet-4-20250514" >&2
            echo "" >&2
            echo "Stack trace (most recent call last):" >&2
            echo "  File \"/app/swival/runner.py\", line 342, in run_loop" >&2
            echo "  File \"/app/swival/llm.py\", line 187, in call_model" >&2
            echo "litellm.BadRequestError: OpenAIException - Invalid model name" >&2
            exit 1
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 30_000,
            MaxStallRetries = 1  // 1 retry → 2 total attempts before flagging
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        // Must be flagged (not valid).
        Assert.False(result.IsValid, "expected flag after retry exhaustion");

        // The error reason must include the tail content (the real error),
        // not just the sandbox banner that TrimForError would capture.
        Assert.Contains("Invalid model name passed in model=balanced",
            result.Error, StringComparison.Ordinal);
        Assert.Contains("litellm.BadRequestError",
            result.Error, StringComparison.Ordinal);

        // The error reason must reference the persisted output path.
        Assert.Contains("stage1-attempt", result.Error, StringComparison.Ordinal);
        Assert.Contains("killed-output.txt", result.Error, StringComparison.Ordinal);

        // The error reason must NOT be just the TrimForError head (which would
        // only show "nono v0.62.0" and the startup banner). It must contain
        // content from the tail, not just the first 600 chars.
        Assert.DoesNotContain("nono v0.62.0", result.Error, StringComparison.Ordinal);

        // Both attempts must leave killed-output artifacts.
        var firstPersisted = Path.Combine(repo.Root, ".relay", "task", "stage1-attempt1.killed-output.txt");
        Assert.True(File.Exists(firstPersisted),
            $"expected killed-output for attempt 1 at {firstPersisted}");
        var firstContent = await File.ReadAllTextAsync(firstPersisted);
        Assert.Contains("Invalid model name", firstContent, StringComparison.Ordinal);

        var secondPersisted = Path.Combine(repo.Root, ".relay", "task", "stage1-attempt2.killed-output.txt");
        Assert.True(File.Exists(secondPersisted),
            $"expected killed-output for attempt 2 at {secondPersisted}");
        var secondContent = await File.ReadAllTextAsync(secondPersisted);
        Assert.Contains("Invalid model name", secondContent, StringComparison.Ordinal);

        // No attempt3 file — retries were exhausted after 2 attempts.
        var thirdPersisted = Path.Combine(repo.Root, ".relay", "task", "stage1-attempt3.killed-output.txt");
        Assert.False(File.Exists(thirdPersisted),
            "attempt 3 should not exist — MaxStallRetries=1 limits to 2 attempts");
    }

    /// <summary>
    /// Pre-flight fail-fast cases must remain unchanged. An always-ready probe
    /// combined with valid resolved commands means the guard path still lets
    /// the spawned process run (and the nonzero-exit retry logic handles it).
    /// This is already covered by the two tests above; this test asserts the
    /// invariant explicitly.
    /// </summary>
    [Fact]
    public async Task RunAsync_NonzeroExit_BoundedBySharedStallBudget()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-always-exit1-bounded",
            """
            #!/usr/bin/env bash
            echo "error line 1" >&2
            echo "error line 2" >&2
            exit 1
            """);
        // MaxStallRetries = 0 — no retries, just persist and flag (same as
        // stall path with MaxStallRetries=0).
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 30_000,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        // Must be flagged on the first (and only) attempt.
        Assert.False(result.IsValid, "expected flag on nonzero exit with no retries");

        // With MaxStallRetries=0, the reason should still include the tail
        // (the actual error), not just a TrimForError head.
        Assert.Contains("error line 1", result.Error, StringComparison.Ordinal);
        Assert.Contains("error line 2", result.Error, StringComparison.Ordinal);

        // The killed-output file must be persisted even without retries.
        var persisted = Path.Combine(repo.Root, ".relay", "task", "stage1-attempt1.killed-output.txt");
        Assert.True(File.Exists(persisted),
            $"expected killed-output at {persisted}");
        var content = await File.ReadAllTextAsync(persisted);
        Assert.Contains("error line 1", content, StringComparison.Ordinal);
        Assert.Contains("error line 2", content, StringComparison.Ordinal);

        // No attempt2 — MaxStallRetries=0 means only one attempt.
        var secondPersisted = Path.Combine(repo.Root, ".relay", "task", "stage1-attempt2.killed-output.txt");
        Assert.False(File.Exists(secondPersisted),
            "attempt 2 should not exist with MaxStallRetries=0");
    }
}
