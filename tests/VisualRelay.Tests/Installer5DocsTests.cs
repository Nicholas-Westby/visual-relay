namespace VisualRelay.Tests;

/// <summary>
/// Tests for README.md and AGENTS.md install documentation. The recommended
/// install path is a source checkout driven by <c>./visual-relay</c> (which
/// bootstraps nix / Determinate Nix and provisions <c>uv</c>/<c>nono</c>);
/// Homebrew is demoted to a "coming once released" note because the formula
/// is not published yet. AGENTS.md still documents the dev-only sample tooling
/// that the (future) Homebrew formula will not ship.
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

    [Fact]
    public void Readme_SourceCheckoutPath_PrecedesBrew()
    {
        var content = ReadReadme();

        var checkoutIdx = content.IndexOf("./visual-relay", StringComparison.Ordinal);
        var brewIdx = content.IndexOf("brew install", StringComparison.Ordinal);

        // The clone + ./visual-relay path is the primary recommendation, so it
        // must appear before any brew install reference.
        Assert.True(checkoutIdx >= 0, "README must document the ./visual-relay launcher");
        Assert.True(brewIdx >= 0, "README must still mention the future brew install");
        Assert.True(
            checkoutIdx < brewIdx,
            "The ./visual-relay source-checkout path must precede the brew install reference");
    }

    [Fact]
    public void Readme_BrewInstall_IsMarkedNotYetAvailable()
    {
        var content = ReadReadme();

        var brewIdx = content.IndexOf("brew install", StringComparison.Ordinal);
        Assert.True(brewIdx >= 0, "README must still mention the future brew install");

        // The brew command is demoted: a window around it must flag that it is
        // not yet available / coming once a release is published, so nobody runs
        // a brew install that 404s today.
        var windowStart = Math.Max(0, brewIdx - 400);
        var window = content.Substring(windowStart, Math.Min(content.Length - windowStart, 800));
        Assert.True(
            window.Contains("not yet", StringComparison.OrdinalIgnoreCase) ||
            window.Contains("once ", StringComparison.OrdinalIgnoreCase) ||
            window.Contains("coming", StringComparison.OrdinalIgnoreCase) ||
            window.Contains("not available", StringComparison.OrdinalIgnoreCase),
            "The brew install must be clearly marked as not yet available / coming once released");
    }

    [Fact]
    public void Readme_BrewInstall_ReferencesTheTap()
    {
        var content = ReadReadme();

        // The future brew command still names the tap formula.
        Assert.Contains("brew install", content, StringComparison.Ordinal);
        Assert.Contains("nicholas-westby/tap/visual-relay", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_InstallSection_WarnsAgainstBrowserDownload()
    {
        var content = ReadReadme();

        // The quarantine/browser-download caveat (relevant to the future brew
        // tarball) must still be explained somewhere in the README.
        Assert.True(
            content.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("quarantine", StringComparison.OrdinalIgnoreCase),
            "README must warn against browser downloads (quarantine re-application)");
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
