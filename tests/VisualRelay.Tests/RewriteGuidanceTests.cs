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
        // Rule #5 must name generic, portable guardrails — never a specific repo's tool.
        Assert.Contains("test suite must pass", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemPrompt_DoesNotContainVisualRelaySpecificIdioms()
    {
        var prompt = RewriteGuidance.SystemPrompt;

        // The prompt flows verbatim to every repo — it must NOT mention
        // Visual Relay by name, its orchestration command, its test framework
        // attributes, or its state-storage conventions.
        Assert.DoesNotContain("./visual-relay check", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Visual Relay task spec", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[AvaloniaFact]", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("XDG, never in-repo", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("C#/XAML files under 300 lines", prompt, StringComparison.Ordinal);
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
