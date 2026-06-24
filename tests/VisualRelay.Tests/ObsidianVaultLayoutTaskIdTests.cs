using VisualRelay.Core.ObsidianBridge;

namespace VisualRelay.Tests;

/// <summary>
/// FIX 1 + FIX 2: path-traversal guards on <see cref="ObsidianVaultLayout"/> — the
/// public <c>IsValidTaskId</c> slug check used at the export boundary, and the
/// hardened repo-name sanitizer that rejects "."/".." (which would hoist the
/// scaffold above the configured vault root). Split from
/// <see cref="ObsidianVaultLayoutTests"/> to stay under the 300-line guard.
/// </summary>
public sealed class ObsidianVaultLayoutTaskIdTests
{
    // ── Repo-name sanitization (FIX 2) ────────────────────────────────

    [Fact]
    public void RepoName_DotDot_FallsBackToProject_DoesNotEscapeVault()
    {
        // A repo dir literally named ".." would hoist the whole scaffold above the
        // configured vault root (Path.Combine(vault, "..")). Must be rejected.
        var layout = new ObsidianVaultLayout("/vault", "..");

        Assert.Equal("/vault/project", layout.RepoDir);
    }

    [Fact]
    public void RepoName_SingleDot_FallsBackToProject()
    {
        var layout = new ObsidianVaultLayout("/vault", ".");

        Assert.Equal("/vault/project", layout.RepoDir);
    }

    [Fact]
    public void RepoName_DotDotWithSeparators_FallsBackToProject()
    {
        // A bare ".." surrounded by separators must collapse to "project" — never
        // leave a ".." component in the resolved path.
        var layout = new ObsidianVaultLayout("/vault", "/../");

        Assert.DoesNotContain("..", layout.RepoDir, StringComparison.Ordinal);
        Assert.StartsWith("/vault/", layout.RepoDir, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoName_OrdinaryNameWithDots_IsKept()
    {
        // Only pure-dot residues are rejected; a normal name containing dots stays.
        var layout = new ObsidianVaultLayout("/vault", "my.repo.v2");

        Assert.Equal("/vault/my.repo.v2", layout.RepoDir);
    }

    // ── GUID-shape rejection (defense in depth) ──────────────────────

    [Fact]
    public void SanitizeRepoName_RejectsLowercaseGuidN()
    {
        // A bare 32-hex-char Guid.ToString("N") is never a legitimate project
        // name — it comes from a temp/worktree checkout whose leaf is a GUID.
        var layout = new ObsidianVaultLayout("/vault", "ada5411c8ffa48d8bcf41340ad6f48af");
        Assert.Equal("/vault/project", layout.RepoDir);
    }

    [Fact]
    public void SanitizeRepoName_RejectsUppercaseGuidN()
    {
        var layout = new ObsidianVaultLayout("/vault", "ADA5411C8FFA48D8BCF41340AD6F48AF");
        Assert.Equal("/vault/project", layout.RepoDir);
    }

    [Fact]
    public void SanitizeRepoName_RejectsMixedCaseGuidN()
    {
        var layout = new ObsidianVaultLayout("/vault", "aDa5411c8FfA48d8BcF41340ad6F48aF");
        Assert.Equal("/vault/project", layout.RepoDir);
    }

    [Fact]
    public void SanitizeRepoName_Allows31HexChars()
    {
        // 31 hex chars is not a full GUID — must pass through.
        var name = "ada5411c8ffa48d8bcf41340ad6f48a";
        var layout = new ObsidianVaultLayout("/vault", name);
        Assert.Equal($"/vault/{name}", layout.RepoDir);
    }

    [Fact]
    public void SanitizeRepoName_Allows33HexChars()
    {
        // 33 hex chars is not a full GUID — must pass through.
        var name = "ada5411c8ffa48d8bcf41340ad6f48af0";
        var layout = new ObsidianVaultLayout("/vault", name);
        Assert.Equal($"/vault/{name}", layout.RepoDir);
    }

    [Fact]
    public void SanitizeRepoName_AllowsNonHexIn32Chars()
    {
        // 32 chars with a 'g' — not hex, not a GUID — must pass through.
        var name = "ada5411c8ffa48d8bcf41340ad6f48ag";
        var layout = new ObsidianVaultLayout("/vault", name);
        Assert.Equal($"/vault/{name}", layout.RepoDir);
    }

    // ── Task-id validation (FIX 1 egress guard) ───────────────────────

    [Theory]
    [InlineData("valid-slug")]
    [InlineData("abc")]
    [InlineData("task-123")]
    [InlineData("a1")]
    public void IsValidTaskId_AcceptsSlugs(string id)
    {
        Assert.True(ObsidianVaultLayout.IsValidTaskId(id));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("x.y")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("UPPER")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape")]
    [InlineData("a..b")]
    public void IsValidTaskId_RejectsUnsafeIds(string id)
    {
        Assert.False(ObsidianVaultLayout.IsValidTaskId(id));
    }
}
