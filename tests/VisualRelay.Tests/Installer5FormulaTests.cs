namespace VisualRelay.Tests;

/// <summary>
/// Tests for <c>packaging/visual-relay.rb</c> (Homebrew formula template).
/// The formula must be a Formula (not Cask), depend on uv, have per-arch URLs,
/// install into libexec, and symlink bin/visual-relay.
/// These must FAIL before the file exists.
/// </summary>
public sealed class Installer5FormulaTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string FormulaPath => Path.Combine(RepoRoot, "packaging", "visual-relay.rb");

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ReadFormula()
    {
        Assert.True(File.Exists(FormulaPath),
            $"Formula file not found at {FormulaPath}. " +
            "It must be created at packaging/visual-relay.rb.");
        return File.ReadAllText(FormulaPath);
    }

    // ── 1. File exists and is a Formula ──────────────────────────────────

    [Fact]
    public void FormulaFile_Exists()
    {
        Assert.True(File.Exists(FormulaPath),
            $"packaging/visual-relay.rb must exist at {FormulaPath}");
    }

    [Fact]
    public void Formula_IsFormulaNotCask()
    {
        var content = ReadFormula();

        // Must inherit from Formula, not Cask.
        Assert.Contains("Formula", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Cask", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Formula_HasCorrectClassName()
    {
        var content = ReadFormula();

        // The class must be VisualRelay < Formula.
        Assert.Contains("VisualRelay", content, StringComparison.Ordinal);
        Assert.Contains("class", content, StringComparison.Ordinal);
    }

    // ── 2. Dependencies ──────────────────────────────────────────────────

    [Fact]
    public void Formula_DependsOnUv()
    {
        var content = ReadFormula();

        // Must declare uv as a dependency (needed by backend.sh).
        Assert.Contains("depends_on", content, StringComparison.Ordinal);
        Assert.Contains("uv", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Formula_DoesNotDependOnDotnet()
    {
        var content = ReadFormula();

        // Must NOT depend on dotnet or dotnet-sdk — the whole point is
        // self-contained publish with no SDK requirement.
        Assert.DoesNotContain("dotnet", content, StringComparison.OrdinalIgnoreCase);
    }

    // ── 3. Per-arch URL selection ────────────────────────────────────────

    [Fact]
    public void Formula_HasPerArchUrlLogic()
    {
        var content = ReadFormula();

        // Must have conditional URL based on architecture (arm vs x86).
        Assert.Contains("Hardware::CPU", content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("arm", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("intel", StringComparison.OrdinalIgnoreCase),
            "Formula must have per-arch URL selection");
    }

    [Fact]
    public void Formula_UrlsPointToGitHubReleases()
    {
        var content = ReadFormula();

        // URLs must reference GitHub Releases artifacts.
        Assert.Contains("github.com", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("releases", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Formula_HasSha256()
    {
        var content = ReadFormula();

        // Must include sha256 for verification.
        Assert.Contains("sha256", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Formula_TarballReferencesCorrectRids()
    {
        var content = ReadFormula();

        // The tarball URLs must include osx-arm64 and osx-x64 RIDs.
        Assert.Contains("osx-arm64", content, StringComparison.Ordinal);
        Assert.Contains("osx-x64", content, StringComparison.Ordinal);
    }

    // ── 4. Install method ────────────────────────────────────────────────

    [Fact]
    public void Formula_InstallsIntoLibexec()
    {
        var content = ReadFormula();

        // Must use libexec.install for the app content.
        Assert.Contains("libexec", content, StringComparison.Ordinal);
        Assert.Contains("install", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Formula_SymlinksBinVisualRelay()
    {
        var content = ReadFormula();

        // Must symlink bin/visual-relay to the launcher script in libexec.
        Assert.Contains("bin", content, StringComparison.Ordinal);
        Assert.Contains("visual-relay", content, StringComparison.Ordinal);
        Assert.Contains("symlink", content, StringComparison.OrdinalIgnoreCase);
    }

    // ── 5. Metadata ──────────────────────────────────────────────────────

    [Fact]
    public void Formula_HasDescription()
    {
        var content = ReadFormula();

        // Must have a desc line.
        Assert.Contains("desc", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Formula_HasHomepage()
    {
        var content = ReadFormula();

        // Must specify homepage.
        Assert.Contains("homepage", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Formula_HasLicense()
    {
        var content = ReadFormula();

        // Must specify license (MIT).
        Assert.Contains("license", content, StringComparison.Ordinal);
    }

    // ── 6. Test block ────────────────────────────────────────────────────

    [Fact]
    public void Formula_HasTestBlock()
    {
        var content = ReadFormula();

        // Should have a test do block (smoke test after install).
        Assert.Contains("test", content, StringComparison.Ordinal);
    }
}
