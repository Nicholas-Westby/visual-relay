using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class SwivalSubagentRunnerCommandFilterTests
{
    private static Task<BackendReadiness> AlwaysReady(CancellationToken _) =>
        Task.FromResult(new BackendReadiness(true, null));

    // ── ResolveCommandsOnPath unit tests ──────────────────────────────

    [Fact]
    public void ResolveCommandsOnPath_AllPresent_PassesThroughByteIdentical()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = Invocation(Path.GetTempPath());

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            "git,ls,cat", sink, invocation);

        Assert.Equal("git,ls,cat", result);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ResolveCommandsOnPath_DropsMissingCommand_EmitsEvent()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = Invocation(Path.GetTempPath());
        var bogus = Guid.NewGuid().ToString("N");
        var commands = $"git,{bogus},ls";

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            commands, sink, invocation);

        Assert.Equal("git,ls", result);

        var dropped = Assert.Single(sink.Events);
        Assert.Equal("command_dropped", dropped.EventName);
        Assert.Equal("warn", dropped.Level);
        Assert.NotNull(dropped.Data);
        Assert.True(dropped.Data.TryGetValue("name", out var name));
        Assert.Equal(bogus, name);
        Assert.True(dropped.Data.TryGetValue("reason", out var reason));
        Assert.Equal("not found on PATH", reason);
    }

    [Fact]
    public void ResolveCommandsOnPath_DropsMultipleMissingCommands_EmitsEventPerDrop()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = Invocation(Path.GetTempPath());
        var bogusA = Guid.NewGuid().ToString("N");
        var bogusB = Guid.NewGuid().ToString("N");
        var commands = $"{bogusA},git,{bogusB},ls";

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            commands, sink, invocation);

        Assert.Equal("git,ls", result);
        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, e => Assert.Equal("command_dropped", e.EventName));
        var droppedNames = sink.Events
            .Select(e => e.Data!["name"])
            .OrderBy(n => n)
            .ToList();
        Assert.Equal(new[] { bogusA, bogusB }.OrderBy(n => n), droppedNames);
    }

    [Fact]
    public void ResolveCommandsOnPath_AllMissing_ReturnsEmpty()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = Invocation(Path.GetTempPath());
        var bogusA = Guid.NewGuid().ToString("N");
        var bogusB = Guid.NewGuid().ToString("N");
        var commands = $"{bogusA},{bogusB}";

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            commands, sink, invocation);

        Assert.Equal("", result);
        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, e => Assert.Equal("command_dropped", e.EventName));
    }

    [Fact]
    public void ResolveCommandsOnPath_All_Passthrough()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = Invocation(Path.GetTempPath());

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            "all", sink, invocation);

        Assert.Equal("all", result);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ResolveCommandsOnPath_None_Passthrough()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = Invocation(Path.GetTempPath());

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            "none", sink, invocation);

        Assert.Equal("none", result);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ResolveCommandsOnPath_NullSink_DoesNotThrow()
    {
        var invocation = Invocation(Path.GetTempPath());
        var bogus = Guid.NewGuid().ToString("N");
        var commands = $"git,{bogus}";

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            commands, eventSink: null, invocation);

        Assert.Equal("git", result);
    }

    [Fact]
    public void ResolveCommandsOnPath_EmptyString_ReturnsEmpty()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = Invocation(Path.GetTempPath());

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            "", sink, invocation);

        Assert.Equal("", result);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ResolveCommandsOnPath_OnlyCommas_ReturnsEmpty()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = Invocation(Path.GetTempPath());

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            ",,,", sink, invocation);

        Assert.Equal("", result);
        Assert.Empty(sink.Events);
    }

    // ── BuildArguments backward compatibility ─────────────────────────

    [Fact]
    public void BuildArguments_WithoutResolvedCommands_UsesStageCommands()
    {
        var config = TestConfig();
        var runner = new SwivalSubagentRunner(config, "swival", backendProbe: AlwaysReady);
        var invocation = Invocation(Path.GetTempPath());

        var args = runner.BuildArguments(invocation);

        var cmdIdx = args.IndexOf("--commands");
        Assert.True(cmdIdx >= 0, "--commands flag missing");
        Assert.Equal(invocation.Stage.Commands, args[cmdIdx + 1]);
    }

    [Fact]
    public void BuildArguments_WithResolvedCommands_OverridesStageCommands()
    {
        var config = TestConfig();
        var runner = new SwivalSubagentRunner(config, "swival", backendProbe: AlwaysReady);
        var invocation = Invocation(Path.GetTempPath());

        var args = runner.BuildArguments(invocation, "git,ls");

        var cmdIdx = args.IndexOf("--commands");
        Assert.True(cmdIdx >= 0, "--commands flag missing");
        Assert.Equal("git,ls", args[cmdIdx + 1]);
    }

    // ── RunAsync integration: some commands missing ───────────────────

    [Fact]
    public async Task RunAsync_SomeCommandsMissing_StillRuns()
    {
        using var repo = TestRepository.Create();
        var bogus = Guid.NewGuid().ToString("N");
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-filter",
            $$"""
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
        var runner = new SwivalSubagentRunner(TestConfig(), script, sink, AlwaysReady);
        var stage = new RelayStageDefinition(
            2, "Research", "cheap", "llm", "some",
            $"git,{bogus},ls",
            "Investigate the codebase.",
            """{ "findings": string, "constraints": string[] }""");
        var invocation = Invocation(repo.Root) with { Stage = stage };

        var result = await runner.RunAsync(invocation);

        Assert.True(result.IsValid);
        Assert.Contains("ran with filtered commands", result.RawText, StringComparison.Ordinal);

        // The fake swival captured the --commands arg; it should be filtered.
        var capturedCommands = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, "commands-arg.txt"));
        Assert.Equal("git,ls", capturedCommands.Trim());

        // A command_dropped event was emitted.
        Assert.Contains(sink.Events, e =>
            e.EventName == "command_dropped" &&
            e.Data is not null &&
            e.Data.TryGetValue("name", out var name) &&
            name == bogus);
    }

    [Fact]
    public async Task RunAsync_AllCommandsMissing_FailsPreSpawn()
    {
        using var repo = TestRepository.Create();
        var bogusA = Guid.NewGuid().ToString("N");
        var bogusB = Guid.NewGuid().ToString("N");
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-never-spawned",
            """
            #!/usr/bin/env bash
            # This script should never be executed — touch a flag file to prove it.
            touch swival-was-spawned.flag
            exit 0
            """);
        var sink = new InMemoryRelayEventSink();
        var runner = new SwivalSubagentRunner(TestConfig(), script, sink, AlwaysReady);
        var stage = new RelayStageDefinition(
            2, "Research", "cheap", "llm", "some",
            $"{bogusA},{bogusB}",
            "Investigate the codebase.",
            """{ "findings": string, "constraints": string[] }""");
        var invocation = Invocation(repo.Root) with { Stage = stage };

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

    // ── Helpers ──────────────────────────────────────────────────────

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
