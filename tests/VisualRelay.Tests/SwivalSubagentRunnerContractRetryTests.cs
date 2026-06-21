using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class SwivalSubagentRunnerContractRetryTests
{
    [Fact]
    public async Task RunAsync_ContractFailureThenRecover_RetriesAndReturnsSuccess()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, sink, SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("recovered on contract retry", result.Json, StringComparison.Ordinal);
        Assert.Contains(sink.Events, e => e.EventName == "contract_retry");
    }

    [Fact]
    public async Task RunAsync_ContractRetry_CorrectivePromptContainsPriorOutput()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, sink, SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var invocation = SwivalTestHelpers.Invocation(repo.Root) with { TaskInput = "Implement the feature." };
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
    public async Task RunAsync_WhenManifestContainsGitignoredPath_TriggersCorrectiveRetry()
    {
        using var repo = TestRepository.Create();
        // Set up a real git repo with a .gitignore so git check-ignore works.
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repo.Root, ".gitignore"), "swival.toml\n");
        // Runtime artifact — exists on disk but is gitignored.
        File.WriteAllText(Path.Combine(repo.Root, "swival.toml"), "[runtime]\nkey = \"val\"");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");

        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-gitignore-retry",
            """
            #!/usr/bin/env bash
            last="${@: -1}"
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            printf '%s' "$last" > "prompt-$(basename "$trace_dir").txt"
            if [[ "$trace_dir" == *attempt2* ]]; then
              printf '```json\n{"plan":"edit tracked files only","manifest":["src/app.cs"]}\n```\n'
              exit 0
            else
              printf '```json\n{"plan":"edit all files","manifest":["swival.toml","src/app.cs"]}\n```\n'
              exit 0
            fi
            """);
        var sink = new InMemoryRelayEventSink();
        var config = TestConfig() with
        {
            MaxContractRetries = 1,
            SubagentTimeoutMilliseconds = 30_000
        };
        var runner = new SwivalSubagentRunner(config, script, sink, SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var stage4 = RelayStages.All[3]; // stage 4 Plan — produces manifest
        var invocation = new StageInvocation(
            stage4, "balanced", "run-1", repo.Root, "task", "# Task",
            string.Empty, [], [],
            Path.Combine(repo.Root, ".relay", "task", "stage4-attempt1"),
            Path.Combine(repo.Root, ".relay", "task", "stage4-attempt1.report.json"),
            1);

        var result = await runner.RunAsync(invocation);

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("edit tracked files only", result.Json, StringComparison.Ordinal);
        Assert.Contains(sink.Events, e => e.EventName == "contract_retry");

        // The corrective prompt must name the gitignored path.
        var correctivePrompt = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, "prompt-stage4-attempt2.txt"));
        Assert.Contains("swival.toml", correctivePrompt, StringComparison.Ordinal);
        Assert.Contains("gitignored", correctivePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ContractRetryExhausted_FlagsWithHint()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.False(result.IsValid);
        Assert.Contains("no valid fenced json block", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MaxContractRetriesZero_PreservesFailFast()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("no valid fenced json block", result.Error, StringComparison.Ordinal);
        // No retry attempt directory must exist.
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay", "task", "stage1-attempt2")),
            "MaxContractRetries=0 must not create a retry attempt directory.");
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
            5_000,
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
