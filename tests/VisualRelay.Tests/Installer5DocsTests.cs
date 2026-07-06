namespace VisualRelay.Tests;

/// <summary>
/// Tests for AGENTS.md contributor documentation. AGENTS.md documents the
/// dev-only sample tooling that the user-facing install path does not ship.
/// </summary>
public sealed class Installer5DocsTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string AgentsPath => Path.Combine(RepoRoot, "AGENTS.md");

    private static string ReadAgents() =>
        File.ReadAllText(AgentsPath);

    // ── AGENTS.md: contributor dev tools ─────────────────────────────────

    [Fact]
    public void Agents_HasSampleTasksSection()
    {
        var content = ReadAgents();

        // AGENTS.md must document sample-reset and run-task as dev-only tools
        // that are NOT shipped in the Homebrew formula.
        Assert.True(
            content.Contains("sample", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Sample Tasks", StringComparison.OrdinalIgnoreCase),
            "AGENTS.md must document sample tasks for contributors");
    }

    [Fact]
    public void Agents_NotesSampleTasksNotShipped()
    {
        var content = ReadAgents();

        // AGENTS.md must note that sample tools are not shipped in brew.
        Assert.True(
            content.Contains("brew", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("formula", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("shipped", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("not included", StringComparison.OrdinalIgnoreCase),
            "AGENTS.md must note sample tools are not shipped in brew formula");
    }
}
