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
