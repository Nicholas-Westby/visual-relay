using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class SwivalSubagentRunnerTests
{
    // These tests exercise the real runner's swival-output handling, not the
    // pre-flight guard, so they inject an always-ready probe rather than depend
    // on a live backend on 127.0.0.1:4000. The guard itself is covered by
    // BackendReadinessProbeTests and SwivalSubagentRunnerGuardTests.

    [Fact]
    public async Task RunAsync_CreatesTemporarySwivalProfileAndKeepsFailureOutput()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival",
            """
            #!/usr/bin/env bash
            test -f swival.toml || exit 17
            echo "profile was available" >&2
            exit 2
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.False(result.IsValid);
        Assert.Contains("swival exit 2", result.Error, StringComparison.Ordinal);
        Assert.Contains("profile was available", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repo.Root, "swival.toml")));
    }

    [Fact]
    public async Task RunAsync_PublishesTraceEntriesFromSwivalJsonl()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-trace",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"live insight"}]}}' > "$trace_dir/trace.jsonl"
            printf '```json\n{"summary":"ok","options":["small"]}\n```\n'
            """);
        var sink = new InMemoryRelayEventSink();
        var runner = new SwivalSubagentRunner(TestConfig(), script, sink, SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.True(result.IsValid);
        Assert.Contains(sink.Events, e =>
            e is { EventName: "trace", Data: not null } &&
            e.Data.TryGetValue("content", out var content) &&
            content == "live insight");
    }

    [Fact]
    public async Task RunAsync_ExtractsJsonWhenStringContainsMarkdownFence()
    {
        using var repo = TestRepository.Create();
        // The manifest lists src/calculator.py, which must exist on disk
        // for the stage-4 existence check to pass.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "calculator.py"), "# exists for manifest validation");
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-json",
            """
            #!/usr/bin/env bash
            printf '```json\n{"plan":"insert a ```python fence inside the plan","manifest":["src/calculator.py"]}\n```\n'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root) with { Stage = RelayStages.All[3] });

        Assert.True(result.IsValid);
        Assert.Contains("```python", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PassesPromptAsRawArgumentWithoutJsonEscaping()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-prompt",
            """
            #!/usr/bin/env bash
            last="${@: -1}"
            printf '%s' "$last" > prompt-capture.txt
            printf '```json\n{"summary":"pass","options":["small"]}\n```\n'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));
        var invocation = SwivalTestHelpers.Invocation(repo.Root) with
        {
            TaskInput = "Implement `multiply(left, right)` and return the product."
        };

        var result = await runner.RunAsync(invocation);

        Assert.True(result.IsValid);
        var captured = await File.ReadAllTextAsync(Path.Combine(repo.Root, "prompt-capture.txt"));
        Assert.Contains("`", captured, StringComparison.Ordinal);
        Assert.Contains("\n", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0060", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("\\n", captured, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExtractsJsonWhenClosingFenceSharesTheJsonLine()
    {
        // Regression: stage 6 of author-edit-and-manage-task-attachments returned
        // a valid single-line JSON object with the closing ``` appended directly
        // to the same line (no newline before it). The old line-based fence
        // scanner required ``` on its own line and rejected the whole stage as
        // "no valid fenced json block", halting the drain. The captured raw
        // output lives in Fixtures/closing-fence-on-content-line.txt.
        using var repo = TestRepository.Create();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "closing-fence-on-content-line.txt");
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-inline-fence",
            $"""
            #!/usr/bin/env bash
            cat '{fixture}'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root) with { Stage = RelayStages.All[5] });

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("Implemented", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FailingVerifyOutput_AppearsInPrompt()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-verify-output",
            """
            #!/usr/bin/env bash
            last="${@: -1}"
            printf '%s' "$last" > prompt-capture.txt
            printf '```json\n{"summary":"fixed verify"}\n```\n'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));
        var invocation = SwivalTestHelpers.Invocation(repo.Root) with
        {
            Stage = RelayStages.All[9], // Stage 10 — Fix-verify
            LastTestOutput = "biome parse error: unexpected token at line 42",
            TestCommand = "bunx biome format && bun test"
        };

        var result = await runner.RunAsync(invocation);

        Assert.True(result.IsValid);
        var captured = await File.ReadAllTextAsync(Path.Combine(repo.Root, "prompt-capture.txt"));
        Assert.Contains("## Failing verify output", captured, StringComparison.Ordinal);
        Assert.Contains("biome parse error: unexpected token at line 42", captured, StringComparison.Ordinal);
        Assert.Contains("## Verify command", captured, StringComparison.Ordinal);
        Assert.Contains("Run this exact command to reproduce and confirm the fix:", captured, StringComparison.Ordinal);
        Assert.Contains("bunx biome format && bun test", captured, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoFailingOutput_NoVerifySection()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-no-verify",
            """
            #!/usr/bin/env bash
            last="${@: -1}"
            printf '%s' "$last" > prompt-capture.txt
            printf '```json\n{"summary":"framed","options":["small"]}\n```\n'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));
        var invocation = SwivalTestHelpers.Invocation(repo.Root) with { Stage = RelayStages.All[0] }; // Stage 1 — Ideate

        var result = await runner.RunAsync(invocation);

        Assert.True(result.IsValid);
        var captured = await File.ReadAllTextAsync(Path.Combine(repo.Root, "prompt-capture.txt"));
        Assert.DoesNotContain("## Failing verify output", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("## Verify command", captured, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_EveryStage_ContainsWorkingDirectoryFact()
    {
        // The harness passes TargetRoot as --base-dir and OS cwd, but the
        // agent-visible prompt never included it, forcing agents to guess
        // (cd /home/user, cd /workspace, ...) costing 2-4 turns per stage.
        // Stage 11 (Commit, Kind="driver") runs in-process with no swival
        // invocation, so it is excluded from the loop.
        var root = "/tmp/test-repo";
        foreach (var stage in RelayStages.All.Where(s => s.Kind != "driver"))
        {
            var invocation = SwivalTestHelpers.Invocation(root) with { Stage = stage };
            var prompt = SwivalSubagentRunner.BuildPrompt(invocation);
            Assert.Contains($"Working directory: {root}", prompt, StringComparison.Ordinal);
        }
    }

    private static RelayConfig TestConfig() =>
        new(
            "llm-tasks",
            "true",
            "true",
            [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            1,
            1,
            1,
            false,
            true,
            0,              // SubagentTimeoutMilliseconds — 0 = disabled (inactivity + maxTurns cover failure modes)
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
