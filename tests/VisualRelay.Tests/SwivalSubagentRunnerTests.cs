using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class SwivalSubagentRunnerTests
{
    // These tests exercise the real runner's swival-output handling, not the
    // pre-flight guard, so they inject an always-ready probe rather than depend
    // on a live backend on 127.0.0.1:4000. The guard itself is covered by
    // BackendReadinessProbeTests and SwivalSubagentRunnerGuardTests.
    private static Task<BackendReadiness> AlwaysReady(CancellationToken _) =>
        Task.FromResult(new BackendReadiness(true, null));

    [Fact]
    public async Task RunAsync_CreatesTemporarySwivalProfileAndKeepsFailureOutput()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival",
            """
            #!/usr/bin/env bash
            test -f swival.toml || exit 17
            echo "profile was available" >&2
            exit 2
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: AlwaysReady);

        var result = await runner.RunAsync(Invocation(repo.Root));

        Assert.False(result.IsValid);
        Assert.Contains("swival exit 2", result.Error, StringComparison.Ordinal);
        Assert.Contains("profile was available", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repo.Root, "swival.toml")));
    }

    [Fact]
    public async Task RunAsync_PublishesTraceEntriesFromSwivalJsonl()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(TestConfig(), script, sink, AlwaysReady);

        var result = await runner.RunAsync(Invocation(repo.Root));

        Assert.True(result.IsValid);
        Assert.Contains(sink.Events, e =>
            e.EventName == "trace" &&
            e.Data is not null &&
            e.Data.TryGetValue("content", out var content) &&
            content == "live insight");
    }

    [Fact]
    public async Task RunAsync_ExtractsJsonWhenStringContainsMarkdownFence()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-json",
            """
            #!/usr/bin/env bash
            printf '```json\n{"plan":"insert a ```python fence inside the plan","manifest":["src/calculator.py"]}\n```\n'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: AlwaysReady);

        var result = await runner.RunAsync(Invocation(repo.Root) with { Stage = RelayStages.All[3] });

        Assert.True(result.IsValid);
        Assert.Contains("```python", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PassesPromptAsRawArgumentWithoutJsonEscaping()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-prompt",
            """
            #!/usr/bin/env bash
            last="${@: -1}"
            printf '%s' "$last" > prompt-capture.txt
            printf '```json\n{"summary":"pass","options":["small"]}\n```\n'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: AlwaysReady);
        var invocation = Invocation(repo.Root) with
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
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-inline-fence",
            $"""
            #!/usr/bin/env bash
            cat '{fixture}'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: AlwaysReady);

        var result = await runner.RunAsync(Invocation(repo.Root) with { Stage = RelayStages.All[5] });

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("Implemented", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FailingVerifyOutput_AppearsInPrompt()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-verify-output",
            """
            #!/usr/bin/env bash
            last="${@: -1}"
            printf '%s' "$last" > prompt-capture.txt
            printf '```json\n{"summary":"fixed verify"}\n```\n'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: AlwaysReady);
        var invocation = Invocation(repo.Root) with
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
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-no-verify",
            """
            #!/usr/bin/env bash
            last="${@: -1}"
            printf '%s' "$last" > prompt-capture.txt
            printf '```json\n{"summary":"framed","options":["small"]}\n```\n'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: AlwaysReady);
        var invocation = Invocation(repo.Root) with { Stage = RelayStages.All[0] }; // Stage 1 — Ideate

        var result = await runner.RunAsync(invocation);

        Assert.True(result.IsValid);
        var captured = await File.ReadAllTextAsync(Path.Combine(repo.Root, "prompt-capture.txt"));
        Assert.DoesNotContain("## Failing verify output", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("## Verify command", captured, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TimeoutWithNoOutput_ReportsStalledBackend()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-stalled-backend",
            """
            #!/usr/bin/env bash
            sleep 60
            """);
        // Totally silent process: no stdout, no stderr, no trace dir.
        // The first-output watchdog kills it at the per-tier window.
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 2_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 15_000,  // backstop
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(Invocation(repo.Root));
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        Assert.Contains("first-output", result.Error, StringComparison.Ordinal);
        Assert.Contains("2000ms", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"Expected kill at ~2 s first-output deadline, took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task RunAsync_TimeoutWithPartialOutput_ReportsHungTestCommand()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-hung-test",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            echo "partial test output" >&2
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"test","message":"running suite"}' > "$trace_dir/trace.jsonl"
            sleep 60
            """);
        // Produces output (disarms first-output watchdog), then goes silent.
        // The inactivity deadline kills it after the per-tier window.
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            InactivityTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 2_000,       // short inactivity window
                ["balanced"] = 600_000,
                ["frontier"] = 1_200_000
            },
            SubagentTimeoutMilliseconds = 30_000,  // backstop
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(Invocation(repo.Root));
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        Assert.Contains("inactivity", result.Error, StringComparison.Ordinal);
        Assert.Contains("2000ms", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"Expected kill at ~2 s inactivity deadline, took {sw.ElapsedMilliseconds} ms");
    }

    private static StageInvocation Invocation(string rootPath) =>
        new(
            RelayStages.All[0],
            "cheap",
            "run-1",
            rootPath,
            "task",
            "# Task",
            string.Empty,
            [],
            [],
            Path.Combine(rootPath, ".relay", "task", "stage1-attempt1"),
            Path.Combine(rootPath, ".relay", "task", "stage1-attempt1.report.json"),
            1);

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
            BypassSandbox: true,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);

    private static async Task<string> WriteExecutableAsync(string rootPath, string name, string text)
    {
        var path = Path.Combine(rootPath, name);
        await File.WriteAllTextAsync(path, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
