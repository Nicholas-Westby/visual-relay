using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests verifying coding-stage system prompts explicitly prohibit the full gate.
/// </summary>
public sealed class CodingStageSystemPromptTests
{
    [Fact]
    public void CodingStageSystemPrompt_ContainsFullGateProhibition()
    {
        // Only Fix-verify still carries the full-gate prohibition;
        // Implement and Fix intentionally removed it so the agent
        // runs the full suite once before declaring done.
        var stage = RelayStages.All.Single(s => s.Name == "Fix-verify");
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
    public void FixVerify_SystemPrompt_InstructsAgentToTreatNonzeroAsRealFailure()
    {
        var stage = RelayStages.All.Single(s => s.Name == "Fix-verify");
        // Must tell the agent that a nonzero exit = real failure even when summary says 0 failed.
        Assert.Contains("nonzero exit", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0 failed", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FixVerify_SystemPrompt_ForbidsRewardHacking_AndHasNonTestGateFallback()
    {
        var stage = RelayStages.All.Single(s => s.Name == "Fix-verify");
        // Must instruct the agent NOT to delete tests or weaken assertions to beat the gate.
        Assert.Contains("do NOT delete tests", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        // Must carry the "report as a non-test gate if not safely fixable" fallback.
        Assert.Contains("non-test gate", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Implement")]
    [InlineData("Fix")]
    [InlineData("Fix-verify")]
    public void CodingStageSystemPrompt_InstructsAgentToTreatNonzeroAsRealFailure(string stageName)
    {
        var stage = RelayStages.All.Single(s => s.Name == stageName);
        // The prompt must tell the agent that a nonzero exit = failure even when
        // the summary reports 0 failed tests.
        Assert.Contains("nonzero", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exit", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfirmImplementationPrompt_ProhibitsReNarrationAndFullGate()
    {
        var prompt = RelayStages.ConfirmImplementationSystemPrompt;

        // Must instruct the agent NOT to re-narrate or re-implement.
        Assert.Contains("do NOT re-narrate", prompt, StringComparison.OrdinalIgnoreCase);

        // Must reference the ## Verify command section so the agent knows where to
        // find the targeted test command.
        Assert.Contains("## Verify command", prompt, StringComparison.Ordinal);
    }

    // ── Visual Relay idiom absence ───────────────────────────────────────

    /// <summary>
    /// All coding-stage system prompts must be portable across repositories.
    /// They must NOT mention <c>./visual-relay check</c> or any other
    /// Visual Relay-specific command, because the prompts flow verbatim to
    /// every repo the tool runs on.
    /// </summary>
    [Theory]
    [InlineData("Author-tests")]
    [InlineData("Implement")]
    [InlineData("Review")]
    [InlineData("Fix")]
    [InlineData("Fix-verify")]
    public void CodingStageSystemPrompt_DoesNotContainVisualRelayCheck(string stageName)
    {
        var stage = RelayStages.All.Single(s => s.Name == stageName);
        Assert.DoesNotContain("./visual-relay check", stage.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmImplementationPrompt_DoesNotContainVisualRelayCheck()
    {
        Assert.DoesNotContain("./visual-relay check",
            RelayStages.ConfirmImplementationSystemPrompt, StringComparison.Ordinal);
    }
}
