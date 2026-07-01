using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests verifying BuildPrompt emits the Verify command section correctly.
/// </summary>
public sealed class BuildPromptVerifyCommandTests
{
    private static StageInvocation MakeInvocation(int stageNumber, string? testCommand) =>
        MakeInvocationWithFull(stageNumber, testCommand, null);

    private static StageInvocation MakeInvocationWithFull(int stageNumber, string? testCommand, string? fullTestCommand) =>
        new(
            Stage: RelayStages.All[stageNumber - 1],
            Tier: "balanced",
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
            TestCommand: testCommand,
            FullTestCommand: fullTestCommand);

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void BuildPrompt_Stage6And8_WithTestCommand_EmitsVerifyCommandSection(int stageNumber)
    {
        var invocation = MakeInvocation(stageNumber, "dotnet test --filter MyFilter");

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.Contains("## Verify command", prompt, StringComparison.Ordinal);
        Assert.Contains("dotnet test --filter MyFilter", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void BuildPrompt_Stage6And8_WithNullTestCommand_DoesNotEmitVerifyCommandSection(int stageNumber)
    {
        var invocation = MakeInvocation(stageNumber, null);

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.DoesNotContain("## Verify command", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Stage9_NullTestCommand_NoVerifySection()
    {
        // Stage 9 (Verify) does not receive a testCommand — regression guard
        var invocation = MakeInvocation(9, null);

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.DoesNotContain("## Verify command", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Stage6_FullDiffersFromTargeted_EmitsFullSuiteBlock()
    {
        var invocation = MakeInvocationWithFull(6, "bun test tests/x.tests.cs", "dotnet test");

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.Contains("## Before you declare done", prompt, StringComparison.Ordinal);
        Assert.Contains("dotnet test", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Stage6_FullEqualsTargeted_OmitsFullSuiteBlock()
    {
        var invocation = MakeInvocationWithFull(6, "dotnet test", "dotnet test");

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.DoesNotContain("## Before you declare done", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Stage6_FullIsNull_OmitsFullSuiteBlock()
    {
        var invocation = MakeInvocationWithFull(6, "bun test tests/x.tests.cs", null);

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.DoesNotContain("## Before you declare done", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Stage8_FullDiffersFromTargeted_EmitsFullSuiteBlock()
    {
        var invocation = MakeInvocationWithFull(8, "bun test tests/x.tests.cs", "dotnet test");

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.Contains("## Before you declare done", prompt, StringComparison.Ordinal);
        Assert.Contains("dotnet test", prompt, StringComparison.Ordinal);
    }
}
