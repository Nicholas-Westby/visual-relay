using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class SwivalSubagentRunnerTests
{
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
        var runner = new SwivalSubagentRunner(TestConfig(), script);

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
            printf '```json\n{"summary":"ok"}\n```\n'
            """);
        var sink = new InMemoryRelayEventSink();
        var runner = new SwivalSubagentRunner(TestConfig(), script, sink);

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
        var runner = new SwivalSubagentRunner(TestConfig(), script);

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
            printf '```json\n{"ok":true}\n```\n'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script);
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
            5_000);

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
