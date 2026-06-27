namespace VisualRelay.Tests;

/// <summary>
/// Tests for README.md and AGENTS.md install documentation. The recommended
/// install path is a source checkout driven by <c>./visual-relay</c>, which
/// bootstraps nix / Determinate Nix and provisions <c>uv</c>/<c>nono</c>.
/// AGENTS.md documents the dev-only sample tooling that the user-facing
/// install path does not ship.
/// </summary>
public sealed class Installer5DocsTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string ReadmePath => Path.Combine(RepoRoot, "README.md");
    private static string AgentsPath => Path.Combine(RepoRoot, "AGENTS.md");

    private static string ReadReadme() =>
        File.ReadAllText(ReadmePath);

    private static string ReadAgents() =>
        File.ReadAllText(AgentsPath);

    // ── README: Install section ──────────────────────────────────────────

    [Fact]
    public void Readme_HasInstallSection()
    {
        var content = ReadReadme();

        // README must have a dedicated "## Install" section.
        Assert.Contains("## Install", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_InstallSection_LeadsWithSourceCheckout()
    {
        var content = ReadReadme();
        var installSection = ExtractSection(content, "## Install");

        // The recommended path is a clone + ./visual-relay run, so the wrapper
        // invocation must be documented in the Install section itself.
        Assert.Contains("./visual-relay", installSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_InstallSection_DocumentsNixBootstrap()
    {
        var content = ReadReadme();
        var installSection = ExtractSection(content, "## Install");

        // The primary path must describe the nix / Determinate-Nix bootstrap
        // that ./visual-relay performs on a source checkout.
        Assert.Contains("nix", installSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Determinate", installSection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readme_InstallSection_DocumentsUvAndNonoPrereqs()
    {
        var content = ReadReadme();
        var installSection = ExtractSection(content, "## Install");

        // The devshell provisions uv (LiteLLM backend) and nono (sandbox); both
        // must be named as prerequisites the bootstrap provides.
        Assert.Contains("uv", installSection, StringComparison.Ordinal);
        Assert.Contains("nono", installSection, StringComparison.Ordinal);
    }

    // ── README: sample-reset / dev-only references removed ───────────────

    [Fact]
    public void Readme_DoesNotReferenceSampleReset()
    {
        var content = ReadReadme();

        // sample-reset must not appear in the user-facing README.
        Assert.DoesNotContain("sample-reset", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_DoesNotReferenceSampleTasksAsUserCommand()
    {
        var content = ReadReadme();

        // The hardcoded /Users/admin/Dev/sample-tasks path must not appear as
        // a user instruction. It's fine if it appears in a note saying "see
        // AGENTS.md for dev tooling", but not as a path the user should use.
        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains("/Users/admin/Dev/sample-tasks", StringComparison.Ordinal))
            {
                // Acceptable only if the line also references AGENTS.md or
                // "contributor" / "dev-only".
                if (!line.Contains("AGENTS.md", StringComparison.Ordinal) &&
                    !line.Contains("contributor", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("dev-only", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Fail(
                        $"README references /Users/admin/Dev/sample-tasks without " +
                        $"noting it's dev-only or pointing to AGENTS.md: '{line.Trim()}'");
                }
            }
        }
    }

    [Fact]
    public void Readme_PointsToAgentsMdForDevTooling()
    {
        var content = ReadReadme();

        // README must reference AGENTS.md for contributor/dev tooling info.
        Assert.Contains("AGENTS.md", content, StringComparison.Ordinal);
    }

    // ── README: init and launch documented for users ─────────────────────

    [Fact]
    public void Readme_DocumentsLaunchForUsers()
    {
        var content = ReadReadme();

        // The shipped commands (launch, init) must still be documented.
        Assert.Contains("launch", content, StringComparison.Ordinal);
        Assert.Contains("init", content, StringComparison.Ordinal);
    }

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

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts a markdown section starting with the given heading and ending
    /// at the next heading of the same or higher level, or end of file.
    /// </summary>
    private static string ExtractSection(string content, string heading)
    {
        var startIdx = content.IndexOf(heading, StringComparison.Ordinal);
        if (startIdx < 0) return string.Empty;

        // Find the next heading at the same or higher level.
        var afterHeading = content[(startIdx + heading.Length)..];
        var nextHeadingIdx = afterHeading.IndexOf("\n## ", StringComparison.Ordinal);
        if (nextHeadingIdx < 0) return afterHeading;

        return afterHeading[..nextHeadingIdx];
    }
}
