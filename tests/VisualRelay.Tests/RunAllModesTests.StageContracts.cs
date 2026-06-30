using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;

namespace VisualRelay.Tests;

/// <summary>
/// Exhaustive contract validation for every relay stage definition:
/// system prompts, output contracts, tier and files-scope invariants,
/// sequential numbering, and kind/scope exclusivity.
/// </summary>
public sealed partial class RunAllModesTests
{
    // ── Run All mode contract ─────────────────────────────────────────────

    [Fact]
    public void RunAllMode_StandardIsDefault()
    {
        // The default RunAllMode must be Standard so existing behaviour is
        // unchanged when no mode is explicitly selected.
        Assert.Equal(RunAllMode.Standard, default(RunAllMode));
    }

    [Fact]
    public void RunAllMode_StandardIsZero()
    {
        // Standard must be the zero-value so the default constructor / unset
        // property produces Standard behaviour.
        Assert.Equal(0, (int)RunAllMode.Standard);
    }

    // ── System prompt invariants ─────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void AllStages_HaveNonEmptySystemPrompt(int number)
    {
        var stage = RelayStages.All.Single(s => s.Number == number);
        Assert.False(string.IsNullOrWhiteSpace(stage.SystemPrompt),
            $"Stage {number} ({stage.Name}) must have a non-empty SystemPrompt");
    }

    [Fact]
    public void CommitStage_HasEmptySystemPrompt()
    {
        var commit = RelayStages.All.Single(s => s.Number == 11);
        // Stage 11 (Commit) is a driver stage with no LLM — its prompt is empty.
        Assert.Equal(string.Empty, commit.SystemPrompt);
    }

    // ── Output contract invariants ───────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void AllLlmStages_HaveNonEmptyOutputContract(int number)
    {
        var stage = RelayStages.All.Single(s => s.Number == number);
        Assert.False(string.IsNullOrWhiteSpace(stage.OutputContract),
            $"Stage {number} ({stage.Name}) must have a non-empty OutputContract");
    }

    [Fact]
    public void CommitStage_HasEmptyOutputContract()
    {
        var commit = RelayStages.All.Single(s => s.Number == 11);
        // Stage 11 (Commit) is a driver stage — no LLM output contract.
        Assert.Equal(string.Empty, commit.OutputContract);
    }

    // ── Tier invariants ──────────────────────────────────────────────────

    private static readonly HashSet<string> ValidTiers =
        new(StringComparer.Ordinal) { "cheap", "balanced", "frontier" };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void AllStages_HaveValidTier(int number)
    {
        var stage = RelayStages.All.Single(s => s.Number == number);
        Assert.True(ValidTiers.Contains(stage.Tier),
            $"Stage {number} ({stage.Name}) has invalid tier '{stage.Tier}'. " +
            $"Must be one of: cheap, balanced, frontier.");
    }

    // ── Files-scope invariants ───────────────────────────────────────────

    private static readonly HashSet<string> ValidFilesScopes =
        new(StringComparer.Ordinal) { "none", "some", "all", "driver" };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void AllStages_HaveValidFilesScope(int number)
    {
        var stage = RelayStages.All.Single(s => s.Number == number);
        Assert.True(ValidFilesScopes.Contains(stage.Files),
            $"Stage {number} ({stage.Name}) has invalid Files scope '{stage.Files}'. " +
            "Must be one of: none, some, all, driver.");
    }

    // ── Commands invariants ──────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void AllStages_HaveNonEmptyCommands(int number)
    {
        var stage = RelayStages.All.Single(s => s.Number == number);
        Assert.False(string.IsNullOrWhiteSpace(stage.Commands),
            $"Stage {number} ({stage.Name}) must have a non-empty Commands string");
    }

    // ── Sequential numbering ─────────────────────────────────────────────

    [Fact]
    public void Stages_AreSequential_OneThroughEleven()
    {
        var numbers = RelayStages.All.Select(s => s.Number).OrderBy(n => n).ToList();

        Assert.Equal(11, numbers.Count);
        for (var i = 0; i < 11; i++)
            Assert.Equal(i + 1, numbers[i]);
    }

    [Fact]
    public void Stages_HaveNoDuplicateNumbers()
    {
        var numbers = RelayStages.All.Select(s => s.Number).ToList();
        Assert.Equal(numbers.Distinct().Count(), numbers.Count);
    }

    // ── Kind exclusivity ─────────────────────────────────────────────────

    [Fact]
    public void OnlyCommitStage_HasDriverKind()
    {
        foreach (var stage in RelayStages.All)
        {
            if (stage.Number == 11)
                Assert.Equal("driver", stage.Kind);
            else
                Assert.Equal("llm", stage.Kind);
        }
    }

    // ── Files-scope exclusivity ──────────────────────────────────────────

    [Fact]
    public void OnlyIdeateAndCommitStages_HaveNoneFilesScope()
    {
        // Stage 1 (Ideate) has files="none" because it ideates purely from
        // training knowledge. Stage 11 (Commit) also has files="none" — it is a
        // driver stage that only runs git commit, no file access needed.
        foreach (var stage in RelayStages.All)
        {
            if (stage.Number is 1 or 11)
                Assert.Equal("none", stage.Files);
            else
                Assert.NotEqual("none", stage.Files);
        }
    }

    // ── Name uniqueness ──────────────────────────────────────────────────

    [Fact]
    public void Stages_HaveUniqueNames()
    {
        var names = RelayStages.All.Select(s => s.Name).ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
    }

    // ── System prompt contains stage-specific keywords ──────────────────

    [Theory]
    [InlineData(1, "Ideate", "options")]
    [InlineData(2, "Research", "findings")]
    [InlineData(3, "Diagnose", "evidence")]
    [InlineData(4, "Plan", "manifest")]
    [InlineData(5, "Author-tests", "tests")]
    [InlineData(6, "Implement", "Implement the change")]
    [InlineData(7, "Review", "Review")]
    [InlineData(8, "Fix", "Resolve every blocker")]
    [InlineData(9, "Verify", "Summarize")]
    [InlineData(10, "Fix-verify", "Fix all failures")]
    public void StageSystemPrompt_ContainsExpectedKeywords(
        int number, string name, string keyword)
    {
        var stage = RelayStages.All.Single(s => s.Number == number);
        Assert.Equal(name, stage.Name);
        Assert.Contains(keyword, stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    // ── Output contract JSON structure ───────────────────────────────────

    [Fact]
    public void AllLlmStageContracts_ReferenceFencedJsonBlock()
    {
        foreach (var stage in RelayStages.All.Where(s => s.Kind == "llm"))
        {
            Assert.Contains("```json", stage.OutputContract, StringComparison.Ordinal);
            Assert.Contains("block", stage.OutputContract, StringComparison.OrdinalIgnoreCase);
        }
    }
}
