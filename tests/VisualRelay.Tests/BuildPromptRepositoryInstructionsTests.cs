using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests verifying BuildPrompt renders the "Repository instructions" section
/// right after the Manifest block when the invocation carries instruction
/// files, and omits the heading entirely otherwise.
/// </summary>
public sealed class BuildPromptRepositoryInstructionsTests
{
    private static StageInvocation MakeInvocation(IReadOnlyList<string> repositoryInstructionFiles) =>
        new(
            Stage: RelayStages.All[1],
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
            RepositoryInstructionFiles: repositoryInstructionFiles);

    [Fact]
    public void BuildPrompt_WithRepositoryInstructionFiles_EmitsHeadingAndBullets()
    {
        var invocation = MakeInvocation(["AGENTS.md", "CONTRIBUTING.md"]);

        var prompt = SandboxedStage.BuildPrompt(invocation);

        Assert.Contains("## Repository instructions", prompt, StringComparison.Ordinal);
        Assert.Contains("- AGENTS.md", prompt, StringComparison.Ordinal);
        Assert.Contains("- CONTRIBUTING.md", prompt, StringComparison.Ordinal);
        Assert.Contains("`conventions`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_WithoutRepositoryInstructionFiles_OmitsHeading()
    {
        var invocation = MakeInvocation([]);

        var prompt = SandboxedStage.BuildPrompt(invocation);

        Assert.DoesNotContain("## Repository instructions", prompt, StringComparison.Ordinal);
    }

    /// <summary>The section sits right after Manifest, before Task context.</summary>
    [Fact]
    public void BuildPrompt_RepositoryInstructionsSection_ComesRightAfterManifest()
    {
        var invocation = MakeInvocation(["AGENTS.md"]) with { TaskContext = "extra context" };

        var prompt = SandboxedStage.BuildPrompt(invocation);

        var manifestIndex = prompt.IndexOf("## Manifest", StringComparison.Ordinal);
        var repoIndex = prompt.IndexOf("## Repository instructions", StringComparison.Ordinal);
        var contextIndex = prompt.IndexOf("## Task context", StringComparison.Ordinal);
        Assert.True(manifestIndex < repoIndex, "Repository instructions must come after Manifest");
        Assert.True(repoIndex < contextIndex, "Repository instructions must come before Task context");
    }
}
