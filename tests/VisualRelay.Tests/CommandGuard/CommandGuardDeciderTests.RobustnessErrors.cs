using System.Text.Json;
using VisualRelay.Core.CommandGuard;

namespace VisualRelay.Tests.CommandGuard;

public sealed partial class CommandGuardDeciderTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Robustness — internal-error fail-closed for git commit (defect 1)
    //
    // Defect 1: a broken/inert guard must NOT silently re-enable the
    // bypass for a git commit.  When an internal error occurs while
    // processing a git-commit payload, the verdict must be DENY
    // (fail-closed) so the skip-hook flags cannot land.  Non-git
    // commands still fail-open (Allow).
    //
    // These tests simulate an internal error via a disposed
    // JsonDocument.  Currently the catch block returns Allow
    // unconditionally (fail-open), so
    // InternalErrorForGitCommit_DeniesNotAllows FAILS — proving
    // the silent-bypass hole.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulates an internal error via a disposed <see cref="JsonDocument"/>.
    /// Accessing any member of the payload will throw
    /// <see cref="ObjectDisposedException"/>, which is caught by
    /// <see cref="CommandGuardDecider.Decide"/>.  For a git-commit
    /// payload the guard must fail CLOSED (Deny), not silently allow
    /// the original command with its hook-bypass flags intact.
    /// </summary>
    [Fact]
    public void Robustness_InternalErrorForGitCommit_DeniesNotAllows()
    {
        var rawJson = """{"phase":"before","mode":"argv","command":["git","commit","-n"]}""";
        var doc = JsonDocument.Parse(rawJson);
        var payload = doc.RootElement;
        doc.Dispose();

        var result = CommandGuardDecider.Decide(payload, rawJson);

        // The guard must fail CLOSED for git commit: a broken guard
        // cannot silently re-enable the hook-bypass it exists to block.
        Assert.False(result.IsAllow,
            "git commit must be DENIED on internal error, not allowed");
    }

    /// <summary>
    /// Companion to the above: non-git commands must still fail-open so a
    /// broken guard never wedges the agent's other work.
    /// </summary>
    [Fact]
    public void Robustness_InternalErrorForNonGit_Allows()
    {
        var rawJson = """{"phase":"before","mode":"argv","command":["ls","-la"]}""";
        var doc = JsonDocument.Parse(rawJson);
        var payload = doc.RootElement;
        doc.Dispose();

        var result = CommandGuardDecider.Decide(payload, rawJson);

        // Non-git commands fail-open — agent can still do normal work.
        Assert.True(result.IsAllow,
            "non-git command must still be allowed on internal error");
    }

    /// <summary>
    /// Shell-mode parity: a broken guard must also fail CLOSED for a
    /// git commit in shell mode.  The detection must match the literal
    /// <c>"git commit …"</c> in the command string, not only quoted
    /// JSON tokens (<c>"git"</c>) as in argv mode.
    /// </summary>
    [Fact]
    public void Robustness_InternalErrorForGitCommit_ShellMode_DeniesNotAllows()
    {
        var rawJson = """{"phase":"before","mode":"shell","command":"git commit -n -m x"}""";
        var doc = JsonDocument.Parse(rawJson);
        var payload = doc.RootElement;
        doc.Dispose();

        var result = CommandGuardDecider.Decide(payload, rawJson);

        // Shell-mode git commit must also fail CLOSED — a broken guard
        // cannot silently re-enable the bypass when the agent uses shell mode.
        Assert.False(result.IsAllow,
            "shell-mode git commit must be DENIED on internal error, not allowed");
    }
}
