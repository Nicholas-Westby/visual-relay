using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class RewriteGuidanceTests
{
    [Fact]
    public void SystemPrompt_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(RewriteGuidance.SystemPrompt),
            "SystemPrompt must be the verbatim rewrite prompt from the spec");
    }

    [Fact]
    public void SystemPrompt_ContainsCoreRules()
    {
        var prompt = RewriteGuidance.SystemPrompt;

        // Must encode the core "what makes a good spec" rules.
        Assert.Contains("succinct", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grounded", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one decided direction", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TDD-first", prompt, StringComparison.Ordinal);
        Assert.Contains("./visual-relay check", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInput_EmbedsSpecAndPath()
    {
        const string currentSpec = "# My Task\n\nDo something useful.\n";
        const string relativePath = "llm-tasks/my-task/my-task.md";

        var input = RewriteGuidance.BuildInput(currentSpec, relativePath);

        // Must embed the current spec verbatim.
        Assert.Contains(currentSpec, input, StringComparison.Ordinal);

        // Must embed the repo-relative path to overwrite.
        Assert.Contains(relativePath, input, StringComparison.Ordinal);

        // Must include the "stay in folder" constraint.
        Assert.Contains("folder", input, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildInput_NeverExpandsScope()
    {
        const string currentSpec = "# My Task\n\nDo something useful.\n";

        var input = RewriteGuidance.BuildInput(currentSpec, "llm-tasks/my-task/my-task.md");

        // Must NOT mention files outside the task's own folder.
        Assert.DoesNotContain(".relay/config.json", input, StringComparison.Ordinal);
        Assert.DoesNotContain("llm-tasks/other-task", input, StringComparison.Ordinal);
        Assert.DoesNotContain("src/VisualRelay.App", input, StringComparison.Ordinal);
    }
}
