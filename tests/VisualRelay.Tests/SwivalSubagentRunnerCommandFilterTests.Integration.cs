using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// Integration tests and RelayStages audit, split out of
// SwivalSubagentRunnerCommandFilterTests.cs to keep each file under the 300-line guard.
public sealed partial class SwivalSubagentRunnerCommandFilterTests
{
    // ── RunAsync integration: some commands missing ───────────────────

    [Fact]
    public async Task RunAsync_SomeCommandsMissing_StillRuns()
    {
        using var repo = TestRepository.Create();
        var bogus = Guid.NewGuid().ToString("N");
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-filter",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--commands" ]]; then
                echo "$2" > commands-arg.txt
                shift 2
              else
                shift
              fi
            done
            printf '```json\n{"findings":"ran with filtered commands","constraints":[]}\n```\n'
            """);
        var sink = new InMemoryRelayEventSink();
        var runner = new SwivalSubagentRunner(TestConfig(), script, sink, SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));
        var stage = new RelayStageDefinition(
            2, "Research", "cheap", "llm", "some",
            $"git,{bogus},ls",
            "Investigate the codebase.",
            """{ "findings": string, "constraints": string[] }""");
        var invocation = SwivalTestHelpers.Invocation(repo.Root) with { Stage = stage };

        var result = await runner.RunAsync(invocation);

        Assert.True(result.IsValid);
        Assert.Contains("ran with filtered commands", result.RawText, StringComparison.Ordinal);

        // The fake swival captured the --commands arg; it should be filtered.
        var capturedCommands = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, "commands-arg.txt"));
        Assert.Equal("git,ls", capturedCommands.Trim());

        // A command_dropped event was emitted.
        Assert.Contains(sink.Events, e =>
            e is { EventName: "command_dropped", Data: not null } &&
            e.Data.TryGetValue("name", out var name) &&
            name == bogus);
    }

    [Fact]
    public async Task RunAsync_AllCommandsMissing_FailsPreSpawn()
    {
        using var repo = TestRepository.Create();
        var bogusA = Guid.NewGuid().ToString("N");
        var bogusB = Guid.NewGuid().ToString("N");
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-never-spawned",
            """
            #!/usr/bin/env bash
            # This script should never be executed — touch a flag file to prove it.
            touch swival-was-spawned.flag
            exit 0
            """);
        var sink = new InMemoryRelayEventSink();
        var runner = new SwivalSubagentRunner(TestConfig(), script, sink, SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));
        var stage = new RelayStageDefinition(
            2, "Research", "cheap", "llm", "some",
            $"{bogusA},{bogusB}",
            "Investigate the codebase.",
            """{ "findings": string, "constraints": string[] }""");
        var invocation = SwivalTestHelpers.Invocation(repo.Root) with { Stage = stage };

        var result = await runner.RunAsync(invocation);

        Assert.False(result.IsValid);
        Assert.Contains("All whitelisted commands are missing from PATH",
            result.Error, StringComparison.Ordinal);
        Assert.Contains(bogusA, result.Error, StringComparison.Ordinal);
        Assert.Contains(bogusB, result.Error, StringComparison.Ordinal);

        // Swival was never spawned.
        Assert.False(File.Exists(Path.Combine(repo.Root, "swival-was-spawned.flag")));

        // Both drops were evented before the early-exit.
        Assert.Equal(2, sink.Events.Count(e => e.EventName == "command_dropped"));
    }

    // ── RelayStages whitelist audit ───────────────────────────────────

    [Fact]
    public void RelayStages_Stages2Through4_DoNotContainRg()
    {
        // Stage 2 — Research
        Assert.DoesNotContain("rg", RelayStages.All[1].Commands, StringComparison.Ordinal);
        Assert.Contains("grep", RelayStages.All[1].Commands, StringComparison.Ordinal);

        // Stage 3 — Diagnose
        Assert.DoesNotContain("rg", RelayStages.All[2].Commands, StringComparison.Ordinal);
        Assert.Contains("grep", RelayStages.All[2].Commands, StringComparison.Ordinal);

        // Stage 4 — Plan
        Assert.DoesNotContain("rg", RelayStages.All[3].Commands, StringComparison.Ordinal);
        Assert.Contains("grep", RelayStages.All[3].Commands, StringComparison.Ordinal);
    }

    [Fact]
    public void RelayStages_Stage1_CommandsUnchanged()
    {
        // Stage 1 Ideate was fine — verify it's untouched.
        Assert.Equal("git,ls,cat", RelayStages.All[0].Commands);
    }
}
