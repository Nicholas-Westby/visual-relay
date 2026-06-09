namespace VisualRelay.Tests;

/// <summary>
/// Tests for README.md and AGENTS.md changes in installer-5:
/// user Install section with brew command, removal of dev-only sample-tasks
/// references from user docs, and sample-tasks documentation in contributor docs.
/// These must FAIL before the implementation lands.
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

        // README must have a dedicated "## Install" section (or similar)
        // before the "## Run" section, with brew install instructions.
        Assert.Contains("## Install", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_InstallSection_ContainsBrewInstallCommand()
    {
        var content = ReadReadme();

        // The install section must contain the full brew install command.
        Assert.Contains("brew install", content, StringComparison.Ordinal);
        Assert.Contains("nicholas-westby/tap/visual-relay", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_InstallSection_MentionsNoSdkRequired()
    {
        var content = ReadReadme();

        // The install section must communicate that no .NET SDK is needed.
        Assert.Contains(".NET", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_InstallSection_WarnsAgainstBrowserDownload()
    {
        var content = ReadReadme();

        // Must warn users to install via brew or curl, never browser download
        // (which re-applies the quarantine attribute).
        var installSection = ExtractSection(content, "## Install");
        Assert.True(
            installSection.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
            installSection.Contains("curl", StringComparison.OrdinalIgnoreCase) ||
            installSection.Contains("quarantine", StringComparison.OrdinalIgnoreCase),
            "Install section must warn against browser downloads (quarantine re-application)");
    }

    [Fact]
    public void Readme_ExplainsFormulaNotCaskRationale()
    {
        var content = ReadReadme();

        // The README must explain why it's a formula, not a cask (no
        // quarantine, no Gatekeeper, no notarization).
        Assert.True(
            content.Contains("formula", StringComparison.OrdinalIgnoreCase) &&
            (content.Contains("cask", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("quarantine", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("Gatekeeper", StringComparison.OrdinalIgnoreCase)),
            "README must explain formula-not-cask rationale");
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
    public void Readme_DocumentsInitAndLaunchForUsers()
    {
        var content = ReadReadme();

        // The shipped commands (init, launch) must be documented for users.
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
