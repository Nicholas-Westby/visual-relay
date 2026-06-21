using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class StageInputArtifactIntegrationTests
{
    [Fact]
    public async Task RunAsync_WritesInputArtifactOnStageStart()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-input-artifact",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
              if [[ "$1" == "--report" ]]; then report_file="$2"; shift 2; else shift; fi
            done
            mkdir -p "$trace_dir"
            printf '```json\n{"summary":"framed","options":["small"]}\n```\n'
            """);
        var sink = new InMemoryRelayEventSink();
        var runner = new SwivalSubagentRunner(
            TestConfig(), script, sink, SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.True(result.IsValid);

        // Verify the .input.json artifact was written
        var expectedInputPath = StageInputArtifact.PathFor(
            SwivalTestHelpers.Invocation(repo.Root).ReportFile);
        Assert.True(File.Exists(expectedInputPath),
            $"Expected .input.json at {expectedInputPath}");

        Assert.True(StageInputArtifact.TryRead(expectedInputPath, out var artifact));
        Assert.NotNull(artifact);
        Assert.Equal(1, artifact.Version);
        Assert.Equal(1, artifact.Stage);
        Assert.Equal(1, artifact.Attempt);
        Assert.Equal("Ideate", artifact.Name);
        Assert.Equal(RelayStages.All[0].SystemPrompt, artifact.SystemPrompt);
        Assert.Contains("Frame the task", artifact.SystemPrompt);
        Assert.Contains("# Relay stage 1: Ideate", artifact.InputPrompt);
        Assert.Contains("Task: task", artifact.InputPrompt);
        Assert.Contains("## Task input", artifact.InputPrompt);
        Assert.Contains("# Task", artifact.InputPrompt);

        // Timestamp should be ISO-8601 UTC (.NET "O" produces +00:00 or Z)
        Assert.StartsWith("202", artifact.Timestamp);
        Assert.Contains("T", artifact.Timestamp);
        Assert.True(artifact.Timestamp.EndsWith("Z") || artifact.Timestamp.EndsWith("+00:00"),
            $"Expected ISO-8601 UTC suffix, got: {artifact.Timestamp}");
    }

    [Fact]
    public async Task RunAsync_EmitsStageInputEventWithMetadataOnly()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-input-event",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            mkdir -p "$trace_dir"
            printf '```json\n{"summary":"framed","options":["small"]}\n```\n'
            """);
        var sink = new InMemoryRelayEventSink();
        var runner = new SwivalSubagentRunner(
            TestConfig(), script, sink, SwivalTestHelpers.AlwaysReady);

        await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        var stageInputEvent = sink.Events.FirstOrDefault(e => e.EventName == "stage_input");
        Assert.NotNull(stageInputEvent);
        Assert.Equal("info", stageInputEvent.Level);
        Assert.Equal("run-1", stageInputEvent.RunId);
        Assert.Equal(repo.Root, stageInputEvent.RootPath);
        Assert.Equal("task", stageInputEvent.TaskId);
        Assert.Equal(1, stageInputEvent.StageNumber);
        Assert.Equal("cheap", stageInputEvent.Tier);
        Assert.Equal(1, stageInputEvent.Attempt);

        Assert.NotNull(stageInputEvent.Data);
        Assert.True(stageInputEvent.Data.ContainsKey("systemBytes"));
        Assert.True(stageInputEvent.Data.ContainsKey("inputBytes"));
        Assert.True(stageInputEvent.Data.ContainsKey("path"));

        // Verify the path points to the .input.json file
        Assert.EndsWith(".input.json", stageInputEvent.Data["path"]);

        // Verify the event does NOT carry the prompt text (only byte lengths)
        Assert.DoesNotContain("Relay stage", stageInputEvent.Data.Values);
    }

    [Fact]
    public async Task RunAsync_FrontLoadedStage6_UsesConfirmImplementationPrompt()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-frontload",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            mkdir -p "$trace_dir"
            printf '```json\n{"summary":"implemented"}\n```\n'
            """);
        var sink = new InMemoryRelayEventSink();

        // RelayDriver bakes the ConfirmImplementation prompt into stage 6
        // before invocation.  Simulate that here by creating a stage 6 with
        // the swapped system prompt.
        var frontLoadedStage = RelayStages.All[5] with
        {
            SystemPrompt = RelayStages.ConfirmImplementationSystemPrompt
        };
        var invocation = SwivalTestHelpers.Invocation(repo.Root) with
        {
            Stage = frontLoadedStage,
            TraceDirectory = Path.Combine(repo.Root, ".relay", "task", "stage6-attempt1"),
            ReportFile = Path.Combine(repo.Root, ".relay", "task", "stage6-attempt1.report.json")
        };

        var runner = new SwivalSubagentRunner(
            TestConfig(), script, sink, SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(invocation);
        Assert.True(result.IsValid);

        var expectedInputPath = StageInputArtifact.PathFor(invocation.ReportFile);
        Assert.True(File.Exists(expectedInputPath));

        Assert.True(StageInputArtifact.TryRead(expectedInputPath, out var artifact));
        Assert.NotNull(artifact);
        Assert.Equal(6, artifact.Stage);
        Assert.Equal("Implement", artifact.Name);

        // Should use the ConfirmImplementation prompt, not the default Implement prompt
        Assert.Equal(RelayStages.ConfirmImplementationSystemPrompt, artifact.SystemPrompt);
        Assert.Contains("Do NOT re-narrate or re-implement", artifact.SystemPrompt);
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
            0,
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2,
            BypassSandbox: true,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
