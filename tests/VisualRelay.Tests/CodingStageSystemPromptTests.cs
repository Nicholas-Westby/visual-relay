using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests verifying coding-stage system prompts explicitly prohibit the full gate.
/// </summary>
public sealed class CodingStageSystemPromptTests
{
    [Theory]
    [InlineData("Implement")]
    [InlineData("Fix")]
    [InlineData("Fix-verify")]
    public void CodingStageSystemPrompt_ContainsFullGateProhibition(string stageName)
    {
        var stage = RelayStages.All.Single(s => s.Name == stageName);
        Assert.Contains("do NOT run", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Implement")]
    [InlineData("Fix")]
    public void ImplementAndFix_SystemPrompt_ReferencesVerifyCommandSection(string stageName)
    {
        var stage = RelayStages.All.Single(s => s.Name == stageName);
        Assert.Contains("## Verify command", stage.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Implement_SystemPrompt_ContainsOriginalIntent()
    {
        var stage = RelayStages.All.Single(s => s.Name == "Implement");
        Assert.Contains("Implement the change", stage.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Fix_SystemPrompt_ContainsOriginalIntent()
    {
        var stage = RelayStages.All.Single(s => s.Name == "Fix");
        Assert.Contains("Resolve every blocker", stage.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void FixVerify_SystemPrompt_MentionsVerifyCommandSection()
    {
        var stage = RelayStages.All.Single(s => s.Name == "Fix-verify");
        Assert.Contains("## Verify command", stage.SystemPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Implement")]
    [InlineData("Fix")]
    [InlineData("Fix-verify")]
    public void CodingStageSystemPrompt_MentionsHarnessRunsFullGate(string stageName)
    {
        var stage = RelayStages.All.Single(s => s.Name == stageName);
        Assert.Contains("harness", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Implement")]
    [InlineData("Fix")]
    [InlineData("Fix-verify")]
    public void CodingStageSystemPrompt_ContainsMinimalEditInstruction(string stageName)
    {
        var stage = RelayStages.All.Single(s => s.Name == stageName);
        Assert.Contains("diff-scoped", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfirmImplementationPrompt_ProhibitsReNarrationAndFullGate()
    {
        var prompt = RelayStages.ConfirmImplementationSystemPrompt;

        // Must instruct the agent NOT to re-narrate or re-implement.
        Assert.Contains("do NOT re-narrate", prompt, StringComparison.OrdinalIgnoreCase);

        // Must retain the full-gate prohibition (same invariant as the canonical
        // Implement prompt).
        Assert.Contains("do NOT run", prompt, StringComparison.OrdinalIgnoreCase);

        // Must reference the ## Verify command section so the agent knows where to
        // find the targeted test command.
        Assert.Contains("## Verify command", prompt, StringComparison.Ordinal);
    }
}
