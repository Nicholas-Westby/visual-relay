using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class SwivalSubagentRunnerCommandFilterTests
{
    // ── ResolveCommandsOnPath unit tests ──────────────────────────────

    [Fact]
    public void ResolveCommandsOnPath_AllPresent_PassesThroughByteIdentical()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            "git,ls,cat", sink, invocation);

        Assert.Equal("git,ls,cat", result);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ResolveCommandsOnPath_DropsMissingCommand_EmitsEvent()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());
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
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());
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
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());
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
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            "all", sink, invocation);

        Assert.Equal("all", result);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ResolveCommandsOnPath_None_Passthrough()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            "none", sink, invocation);

        Assert.Equal("none", result);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ResolveCommandsOnPath_NullSink_DoesNotThrow()
    {
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());
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
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());

        var result = SwivalSubagentRunner.ResolveCommandsOnPath(
            "", sink, invocation);

        Assert.Equal("", result);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ResolveCommandsOnPath_OnlyCommas_ReturnsEmpty()
    {
        var sink = new InMemoryRelayEventSink();
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());

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
        var runner = new SwivalSubagentRunner(config, backendProbe: SwivalTestHelpers.AlwaysReady);
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());

        var args = runner.BuildArguments(invocation);

        var cmdIdx = args.IndexOf("--commands");
        Assert.True(cmdIdx >= 0, "--commands flag missing");
        Assert.Equal(invocation.Stage.Commands, args[cmdIdx + 1]);
    }

    [Fact]
    public void BuildArguments_WithResolvedCommands_OverridesStageCommands()
    {
        var config = TestConfig();
        var runner = new SwivalSubagentRunner(config, backendProbe: SwivalTestHelpers.AlwaysReady);
        var invocation = SwivalTestHelpers.Invocation(Path.GetTempPath());

        var args = runner.BuildArguments(invocation, "git,ls");

        var cmdIdx = args.IndexOf("--commands");
        Assert.True(cmdIdx >= 0, "--commands flag missing");
        Assert.Equal("git,ls", args[cmdIdx + 1]);
    }

    private static RelayConfig TestConfig() =>
        new(
            "llm-tasks",
            "true",
            "true",
            [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true,
            1,
            1,
            false,
            true,
            5_000,
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2);
}
