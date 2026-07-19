using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests verifying that BuildPrompt names <c>.relay/scratch/</c> as the
/// canonical scratch area and no longer mentions the legacy
/// <c>.relay-scratch/</c> path.
/// </summary>
public sealed class BuildPromptScratchGuidanceTests
{
    [Fact]
    public void BuildPrompt_WithTasksDir_ContainsRelayScratchGuidance()
    {
        var invocation = new StageInvocation(
            Stage: RelayStages.All[0],
            Tier: "cheap",
            RunId: "run-1",
            TargetRoot: "/tmp/root",
            TaskName: "test-task",
            TaskInput: "# Test task",
            LedgerSoFar: string.Empty,
            Manifest: ["src/app.cs"],
            LogSources: [],
            TraceDirectory: "/tmp/trace",
            ReportFile: "/tmp/report.json",
            MaxTurns: 200,
            TasksDir: "llm-tasks");

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.Contains(".relay/scratch/", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_WithTasksDir_DoesNotContainLegacyRelayScratch()
    {
        var invocation = new StageInvocation(
            Stage: RelayStages.All[0],
            Tier: "cheap",
            RunId: "run-1",
            TargetRoot: "/tmp/root",
            TaskName: "test-task",
            TaskInput: "# Test task",
            LedgerSoFar: string.Empty,
            Manifest: ["src/app.cs"],
            LogSources: [],
            TraceDirectory: "/tmp/trace",
            ReportFile: "/tmp/report.json",
            MaxTurns: 200,
            TasksDir: "llm-tasks");

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.DoesNotContain(".relay-scratch", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_WithoutTasksDir_DoesNotContainEitherScratchReference()
    {
        // When there's no TasksDir, the protected-paths line is omitted entirely.
        var invocation = new StageInvocation(
            Stage: RelayStages.All[0],
            Tier: "cheap",
            RunId: "run-1",
            TargetRoot: "/tmp/root",
            TaskName: "test-task",
            TaskInput: "# Test task",
            LedgerSoFar: string.Empty,
            Manifest: ["src/app.cs"],
            LogSources: [],
            TraceDirectory: "/tmp/trace",
            ReportFile: "/tmp/report.json",
            MaxTurns: 200,
            TasksDir: null);

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.DoesNotContain(".relay-scratch", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(".relay/scratch", prompt, StringComparison.Ordinal);
    }
}
