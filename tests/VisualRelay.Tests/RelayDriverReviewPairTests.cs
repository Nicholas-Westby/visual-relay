using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests that verify the parallel Visual-review stage definition, renumbering,
/// prompt content, and pair-orchestration behaviour.
/// </summary>
public sealed partial class RelayDriverReviewPairTests
{
    // ── Stage table shape after inserting Visual-review at position 8 ─────

    [Fact]
    public void Stages_CountIsTwelve()
    {
        Assert.Equal(12, RelayStages.All.Count);
    }

    [Fact]
    public void Stages_AreSequential_OneThroughTwelve()
    {
        var numbers = RelayStages.All.Select(s => s.Number).OrderBy(n => n).ToList();

        Assert.Equal(12, numbers.Count);
        for (var i = 0; i < 12; i++)
            Assert.Equal(i + 1, numbers[i]);
    }

    [Theory]
    [InlineData(1, "Ideate", "cheap")]
    [InlineData(2, "Research", "cheap")]
    [InlineData(3, "Diagnose", "balanced")]
    [InlineData(4, "Plan", "balanced")]
    [InlineData(5, "Author-tests", "balanced")]
    [InlineData(6, "Implement", "balanced")]
    [InlineData(7, "Review", "frontier")]
    [InlineData(8, "Visual-review", "vision")]
    [InlineData(9, "Fix", "balanced")]
    [InlineData(10, "Verify", "cheap")]
    [InlineData(11, "Fix-verify", "balanced")]
    [InlineData(12, "Commit", "cheap")]
    public void Stage_HasCorrectNumberNameAndTier(int number, string name, string tier)
    {
        var stage = RelayStages.All.Single(s => s.Number == number);
        Assert.Equal(name, stage.Name);
        Assert.Equal(tier, stage.Tier);
    }

    // ── Visual-review stage definition ────────────────────────────────────

    [Fact]
    public void VisualReviewStage_HasCorrectFilesAndCommands()
    {
        var stage = RelayStages.All.Single(s => s.Number == 8);

        Assert.Equal("Visual-review", stage.Name);
        Assert.Equal("vision", stage.Tier);
        Assert.Equal("llm", stage.Kind);
        Assert.Equal("some", stage.Files);
        Assert.Equal("git,ls,cat", stage.Commands);
    }

    [Fact]
    public void VisualReviewStage_HasVerdictIssuesContract()
    {
        var stage = RelayStages.All.Single(s => s.Number == 8);

        // Visual-review uses the same output contract shape as Review
        // (verdict / issues) so Fix can consume both uniformly.
        Assert.Contains("verdict", stage.OutputContract, StringComparison.Ordinal);
        Assert.Contains("issues", stage.OutputContract, StringComparison.Ordinal);
        Assert.Contains("```json", stage.OutputContract, StringComparison.Ordinal);
    }

    // ── Visual-review system prompt content ───────────────────────────────

    [Fact]
    public void VisualReviewSystemPrompt_ContainsRenderedScreenshotsInstruction()
    {
        var stage = RelayStages.All.Single(s => s.Number == 8);

        Assert.Contains("rendered", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("screenshot", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualReviewSystemPrompt_ContainsViewImageInstruction()
    {
        var stage = RelayStages.All.Single(s => s.Number == 8);

        Assert.Contains("view_image", stage.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualReviewSystemPrompt_ContainsDoNotEditFiles()
    {
        var stage = RelayStages.All.Single(s => s.Number == 8);

        Assert.Contains("Do not edit files", stage.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualReviewSystemPrompt_ContainsVisualDefectsGuidance()
    {
        var stage = RelayStages.All.Single(s => s.Number == 8);

        Assert.Contains("visual defects", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualReviewSystemPrompt_ContainsPassFastCleanExit()
    {
        var stage = RelayStages.All.Single(s => s.Number == 8);

        // The prompt must tell the model to return pass immediately for
        // non-visual changes — a fast clean exit is the expected common case.
        Assert.Contains("pass", stage.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("common case", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualReviewSystemPrompt_ContainsNeverManufactureFindings()
    {
        var stage = RelayStages.All.Single(s => s.Number == 8);

        Assert.Contains("never manufacture findings", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualReviewSystemPrompt_MentionsParallelTextReview()
    {
        var stage = RelayStages.All.Single(s => s.Number == 8);

        // The prompt must clarify that the parallel text Review covers code
        // style/correctness, so Visual-review should not duplicate that.
        Assert.Contains("text", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Review", stage.SystemPrompt, StringComparison.Ordinal);
    }

    // ── Fix prompt updated for both review sources ────────────────────────

    [Fact]
    public void FixStage_NumberIsNine()
    {
        var fix = RelayStages.All.Single(s => s.Number == 9);
        Assert.Equal("Fix", fix.Name);
    }

    [Fact]
    public void FixSystemPrompt_StartsWithResolveEveryBlockerAndWarningFromReviewAndVisualReview()
    {
        var fix = RelayStages.All.Single(s => s.Number == 9);

        Assert.StartsWith(
            "Resolve every blocker and warning from review and visual review.",
            fix.SystemPrompt,
            StringComparison.Ordinal);
    }

    // ── Renumbering: post-Visual-review stage positions ───────────────────

    [Fact]
    public void VerifyStage_NumberIsTen()
    {
        var verify = RelayStages.All.Single(s => s.Number == 10);
        Assert.Equal("Verify", verify.Name);
    }

    [Fact]
    public void FixVerifyStage_NumberIsEleven()
    {
        var fixVerify = RelayStages.All.Single(s => s.Number == 11);
        Assert.Equal("Fix-verify", fixVerify.Name);
    }

    [Fact]
    public void CommitStage_NumberIsTwelve()
    {
        var commit = RelayStages.All.Single(s => s.Number == 12);
        Assert.Equal("Commit", commit.Name);
        Assert.Equal("driver", commit.Kind);
    }

    // ── Driver-kind exclusivity at new position ───────────────────────────

    [Fact]
    public void OnlyCommitStage_HasDriverKind()
    {
        foreach (var stage in RelayStages.All)
        {
            Assert.Equal(stage.Number == 12 ? "driver" : "llm", stage.Kind);
        }
    }

    // ── Files-none exclusivity at new positions ───────────────────────────

    [Fact]
    public void OnlyIdeateAndCommitStages_HaveNoneFilesScope()
    {
        foreach (var stage in RelayStages.All)
        {
            if (stage.Number is 1 or 12)
                Assert.Equal("none", stage.Files);
            else
                Assert.NotEqual("none", stage.Files);
        }
    }

    // ── Vision tier validity ──────────────────────────────────────────────

    [Fact]
    public void VisualReviewStage_UsesVisionTier()
    {
        var stage = RelayStages.All.Single(s => s.Number == 8);
        Assert.Equal("vision", stage.Tier);
    }

    [Fact]
    public void VisionTier_ConfiguredInDefaults()
    {
        var defaults = RelayConfigLoader.Defaults();
        Assert.True(defaults.TierProfiles.TryGetValue("vision", out var profile));
        Assert.Equal("vision", profile);
    }
}

