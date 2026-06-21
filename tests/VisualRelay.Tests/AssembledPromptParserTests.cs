using VisualRelay.App.Services;

namespace VisualRelay.Tests;

public sealed class AssembledPromptParserTests
{
    [Fact]
    public void Parse_NullInput_ReturnsEmpty()
    {
        Assert.Empty(AssembledPromptParser.Parse(null!));
    }

    [Fact]
    public void Parse_EmptyOrWhitespaceOrNoHeadings_ReturnsSinglePromptSection()
    {
        var r1 = AssembledPromptParser.Parse("");
        Assert.Equal("Prompt", Assert.Single(r1).Title);

        var r2 = AssembledPromptParser.Parse("   \n  \n  ");
        Assert.Equal("Prompt", Assert.Single(r2).Title);
        Assert.Equal("   \n  \n  ", r2[0].Body);

        // Text with no "## " headings → single "Prompt" section.
        var r3 = AssembledPromptParser.Parse("# Not a ## heading\nJust text.");
        Assert.Equal("Prompt", Assert.Single(r3).Title);
        Assert.Equal("# Not a ## heading\nJust text.", r3[0].Body);
    }

    [Fact]
    public void Parse_MinimalPrompt_WithTaskInputAndPriorStages()
    {
        var prompt = string.Join('\n',
            "# Relay stage 1: Ideate", "Task: my-task", "Working directory: /root",
            "", "## Task input", "Do the thing.",
            "", "## Prior stages", "(empty — first stage)", "",
            "End your reply with a single fenced ```json block");

        var result = AssembledPromptParser.Parse(prompt);
        Assert.Equal(4, result.Count);
        Assert.Equal("Header", result[0].Title);
        Assert.Contains("# Relay stage 1: Ideate", result[0].Body);
        Assert.Equal("Task input", result[1].Title);
        Assert.Equal("Do the thing.", result[1].Body);
        Assert.Equal("Prior stages", result[2].Title);
        Assert.True(result[2].CollapsedByDefault);
        Assert.Equal("Output contract", result[3].Title);
        Assert.Contains("End your reply", result[3].Body);
    }

    [Fact]
    public void Parse_FullPrompt_WithAllSections()
    {
        var prompt = string.Join('\n',
            "# Relay stage 5: Author-tests",
            "Task: stage-visibility-2", "Working directory: /Users/admin/Dev/visual-relay",
            "", "## Task input", "# Add a StageDetailViewModel", "Full task description...",
            "", "## Manifest", "src/VisualRelay.App/Services/AssembledPromptParser.cs",
            "src/VisualRelay.App/ViewModels/StageDetailViewModel.cs",
            "", "## Task context", "Additional context here.",
            "", "## Log sources", "logs/app.log",
            "", "## Prior stages",
            "## Stage 1 - Ideate", "```json", """{"summary": "..."}""", "```",
            "", "## Stage 2 - Research", "```json", """{"findings": "..."}""", "```",
            "", """End your reply with a single fenced ```json block, nothing after it, matching: {"testFiles": string[], "rationale": string}""",
            "", "## Failing verify output", "FAILED test output here",
            "", "## Verify command", "Run this exact command to reproduce and confirm the fix:",
            "dotnet test --filter AssembledPromptParserTests");

        var result = AssembledPromptParser.Parse(prompt);
        Assert.Equal(9, result.Count);
        Assert.Equal("Header", result[0].Title);
        Assert.Equal("Task input", result[1].Title);
        Assert.Equal("Manifest", result[2].Title);
        Assert.Equal("Task context", result[3].Title);
        Assert.Equal("Log sources", result[4].Title);
        Assert.Equal("Prior stages", result[5].Title);
        Assert.True(result[5].CollapsedByDefault);
        Assert.Contains("## Stage 1 - Ideate", result[5].Body);
        Assert.DoesNotContain("End your reply", result[5].Body);
        Assert.Equal("Output contract", result[6].Title);
        Assert.Contains("End your reply", result[6].Body);
        Assert.Equal("Failing verify output", result[7].Title);
        Assert.Equal("Verify command", result[8].Title);
        Assert.Contains("dotnet test", result[8].Body);
    }

    [Fact]
    public void Parse_MissingOptionalSections_ReturnsOnlyPresentSections()
    {
        var prompt = string.Join('\n',
            "# Relay stage 2: Research", "Task: research-task", "Working directory: /tmp",
            "", "## Task input", "Research the codebase.",
            "", "## Manifest", "(not set yet)",
            "", "## Prior stages", "## Stage 1 - Ideate", "previous output here",
            "", """{"summary": "options"}""");

        var result = AssembledPromptParser.Parse(prompt);
        Assert.Equal(5, result.Count);
        Assert.Equal("Header", result[0].Title);
        Assert.Equal("Task input", result[1].Title);
        Assert.Equal("Manifest", result[2].Title);
        Assert.Equal("Prior stages", result[3].Title);
        Assert.Equal("Output contract", result[4].Title);
    }

    [Fact]
    public void Parse_PriorStagesCollapsedByDefault_OthersFalse()
    {
        var prompt = string.Join('\n',
            "## Task input", "Input here.",
            "", "## Prior stages", "## Stage 1 - Ideate", "Ledger body.",
            "", "Contract line.");

        var result = AssembledPromptParser.Parse(prompt);
        Assert.True(Assert.Single(result, s => s.Title == "Prior stages").CollapsedByDefault);
        foreach (var s in result)
            if (s.Title != "Prior stages")
                Assert.False(s.CollapsedByDefault);
    }

    [Fact]
    public void Parse_NoPriorStagesSection_NoCollapsedSection()
    {
        var result = AssembledPromptParser.Parse("## Task input\nHello world.");
        Assert.Single(result);
        Assert.False(result[0].CollapsedByDefault);
    }

    [Fact]
    public void Parse_OutputContract_ExtractedFromPriorStagesBody()
    {
        var prompt = string.Join('\n',
            "## Prior stages", "Stage 1 output here.", "Stage 2 output here.", "",
            """End your reply with a single fenced ```json block, nothing after it, matching: {"plan": string}""");

        var result = AssembledPromptParser.Parse(prompt);
        Assert.Equal("Stage 1 output here.\nStage 2 output here.",
            Assert.Single(result, s => s.Title == "Prior stages").Body);
        var contract = Assert.Single(result, s => s.Title == "Output contract");
        Assert.Contains("End your reply", contract.Body);
        Assert.False(contract.CollapsedByDefault);
    }

    [Fact]
    public void Parse_PriorStagesWithNoContractLine_NoOutputContractSection()
    {
        var prompt = string.Join('\n',
            "## Prior stages", "## Stage 1 - Ideate",
            "Just a ledger body.", "No blank-line separator here.");
        var result = AssembledPromptParser.Parse(prompt);
        Assert.Contains(result, s => s.Title == "Prior stages");
        Assert.DoesNotContain(result, s => s.Title == "Output contract");
    }

    [Fact]
    public void Parse_BodyTrimming_TrimsSectionBodies()
    {
        var result = AssembledPromptParser.Parse(
            "## Task input\n  \n  content with surrounding whitespace  \n  \n\n## Manifest\n  file.cs  ");
        Assert.Equal("content with surrounding whitespace",
            Assert.Single(result, s => s.Title == "Task input").Body);
        Assert.Equal("file.cs",
            Assert.Single(result, s => s.Title == "Manifest").Body);
    }

    [Fact]
    public void Parse_PriorStagesIsLastSection_ContractExtracted()
    {
        var prompt = string.Join('\n',
            "## Task input", "hello",
            "", "## Prior stages", "## Stage 1 - Ideate", "Stage 1 output.",
            "", """Contract: {"x": string}""");
        var result = AssembledPromptParser.Parse(prompt);
        Assert.Equal(3, result.Count);
        Assert.Equal("Output contract", result[^1].Title);
    }

    [Fact]
    public void Parse_ConsecutiveBlankLines_Handled()
    {
        var result = AssembledPromptParser.Parse(
            "## Task input\n\n\n\ncontent\n\n\n\n## Manifest\n\nfile.cs\n\n");
        Assert.Equal("content",
            Assert.Single(result, s => s.Title == "Task input").Body);
        Assert.Equal("file.cs",
            Assert.Single(result, s => s.Title == "Manifest").Body);
    }

    [Fact]
    public void Parse_HeadingWithNoBody_EmptyBody()
    {
        var result = AssembledPromptParser.Parse("## Task input\n\n## Manifest\nhas content");
        Assert.Equal("", Assert.Single(result, s => s.Title == "Task input").Body);
        Assert.Equal("has content", Assert.Single(result, s => s.Title == "Manifest").Body);
    }
}
