using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class SwivalSubagentRunnerContractRetryTests
{
    private static Task<BackendReadiness> AlwaysReady(CancellationToken _) =>
        Task.FromResult(new BackendReadiness(true, null));

    [Fact]
    public async Task RunAsync_ContractFailureThenRecover_RetriesAndReturnsSuccess()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-contract-retry",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            if [[ "$trace_dir" == *attempt2* ]]; then
              printf '```json\n{"summary":"recovered on contract retry","options":["small"]}\n```\n'
              exit 0
            else
              echo "Here is my analysis but I forgot the fenced JSON block entirely."
              exit 0
            fi
            """);
        var sink = new InMemoryRelayEventSink();
        var config = TestConfig() with
        {
            MaxContractRetries = 1,
            SubagentTimeoutMilliseconds = 30_000
        };
        var runner = new SwivalSubagentRunner(config, script, sink, AlwaysReady);

        var result = await runner.RunAsync(Invocation(repo.Root));

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("recovered on contract retry", result.Json, StringComparison.Ordinal);
        Assert.Contains(sink.Events, e => e.EventName == "contract_retry");
    }

    [Fact]
    public async Task RunAsync_ContractRetry_CorrectivePromptContainsPriorOutput()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-contract-prompt",
            """
            #!/usr/bin/env bash
            last="${@: -1}"
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            printf '%s' "$last" > "prompt-$(basename "$trace_dir").txt"
            if [[ "$trace_dir" == *attempt2* ]]; then
              printf '```json\n{"summary":"corrected on retry","options":["small"]}\n```\n'
              exit 0
            else
              echo "Analysis without contract block."
              exit 0
            fi
            """);
        var sink = new InMemoryRelayEventSink();
        var config = TestConfig() with
        {
            MaxContractRetries = 1,
            SubagentTimeoutMilliseconds = 30_000
        };
        var runner = new SwivalSubagentRunner(config, script, sink, AlwaysReady);

        var invocation = Invocation(repo.Root) with { TaskInput = "Implement the feature." };
        var result = await runner.RunAsync(invocation);

        Assert.True(result.IsValid);
        Assert.Contains("corrected on retry", result.Json, StringComparison.Ordinal);

        // The corrective prompt (attempt 2) must contain the prior output
        // and the instruction to reply with ONLY the fenced JSON block.
        var correctivePrompt = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, "prompt-stage1-attempt2.txt"));
        Assert.Contains("Analysis without contract block", correctivePrompt, StringComparison.Ordinal);
        Assert.Contains("reply with ONLY", correctivePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ContractRetryExhausted_FlagsWithHint()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-contract-exhaust",
            """
            #!/usr/bin/env bash
            echo "Still no contract block, sorry."
            exit 0
            """);
        var config = TestConfig() with
        {
            MaxContractRetries = 1,
            SubagentTimeoutMilliseconds = 30_000
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var result = await runner.RunAsync(Invocation(repo.Root));

        Assert.False(result.IsValid);
        Assert.Contains("no valid fenced json block", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MaxContractRetriesZero_PreservesFailFast()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-contract-failfast",
            """
            #!/usr/bin/env bash
            echo "No contract block."
            exit 0
            """);
        var config = TestConfig() with
        {
            MaxContractRetries = 0,
            SubagentTimeoutMilliseconds = 30_000
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(Invocation(repo.Root));
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("no valid fenced json block", result.Error, StringComparison.Ordinal);
        // No retry attempt directory must exist.
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay", "task", "stage1-attempt2")),
            "MaxContractRetries=0 must not create a retry attempt directory.");
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
            5_000,
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2,
            BypassSandbox: true);

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
