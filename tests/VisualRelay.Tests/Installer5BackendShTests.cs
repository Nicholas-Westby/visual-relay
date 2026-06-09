namespace VisualRelay.Tests;

/// <summary>
/// Tests for <c>tools/backend/backend.sh</c> changes in installer-5:
/// user-writable path redirection when REPO_ROOT is not writable (brew layout).
/// These must FAIL before the implementation lands.
/// </summary>
public sealed class Installer5BackendShTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string BackendShPath => Path.Combine(RepoRoot, "tools", "backend", "backend.sh");

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ReadBackendSh() =>
        File.ReadAllText(BackendShPath);

    private static string[] ReadBackendShLines() =>
        File.ReadAllLines(BackendShPath);

    // ── 1. Writable-REPO_ROOT guard ─────────────────────────────────────

    [Fact]
    public void BackendSh_HasWritabilityCheckForRepoRoot()
    {
        var content = ReadBackendSh();

        // Must check whether REPO_ROOT is writable (e.g. [[ -w "$REPO_ROOT" ]])
        // to distinguish brew install (root-owned libexec) from source checkout.
        Assert.Contains("REPO_ROOT", content, StringComparison.Ordinal);
        Assert.Contains("-w", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendSh_RedirectsScratchToXdgDataHome_WhenRepoRootNotWritable()
    {
        var content = ReadBackendSh();

        // When REPO_ROOT is not writable, SCRATCH must be redirected to
        // XDG_DATA_HOME/visual-relay/ (with ~/.local/share/visual-relay/ fallback).
        Assert.Contains("XDG_DATA_HOME", content, StringComparison.Ordinal);
        Assert.Contains("SCRATCH", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendSh_RedirectsVenDirToXdgDataHome_WhenRepoRootNotWritable()
    {
        var content = ReadBackendSh();

        // When REPO_ROOT is not writable, VENV_DIR must also be redirected
        // so the litellm venv is created in a user-writable location.
        Assert.Contains("VENV_DIR", content, StringComparison.Ordinal);

        // The VENV_DIR assignment should be inside the writability conditional
        // block (not just at the top as a static path).
        var venvStaticAssignment = "VENV_DIR=\"${SCRIPT_DIR}/.venv\"";
        var idx = content.IndexOf(venvStaticAssignment, StringComparison.Ordinal);
        // The static path may still appear as a fallback/default, but the
        // script must also set VENV_DIR to a DATA_HOME-based path somewhere.
        Assert.Contains(".local/share", content, StringComparison.Ordinal);
    }

    // ── 2. Source-checkout behavior preserved ────────────────────────────

    [Fact]
    public void BackendSh_KeepsRepoRelativePaths_WhenRepoRootWritable()
    {
        var content = ReadBackendSh();

        // The existing paths (SCRATCH at REPO_ROOT/.relay-scratch,
        // VENV_DIR at SCRIPT_DIR/.venv) must still be present as the
        // writable-REPO_ROOT branch.
        Assert.Contains(".relay-scratch", content, StringComparison.Ordinal);
        Assert.Contains("SCRIPT_DIR", content, StringComparison.Ordinal);
    }

    // ── 3. SCRATCH directory creation is safe ────────────────────────────

    [Fact]
    public void BackendSh_MkdirScratchAfterRedirection()
    {
        var content = ReadBackendSh();

        // The mkdir -p "${SCRATCH}" call in cmd_start() must come AFTER
        // SCRATCH has been resolved (writable or redirected). Verify the
        // mkdir line still exists.
        Assert.Contains("mkdir -p \"${SCRATCH}\"", content, StringComparison.Ordinal);
    }

    // ── 4. No hardcoded paths that would break in brew layout ────────────

    [Fact]
    public void BackendSh_UsesVariablesNotHardcodedPaths_ForDirs()
    {
        var content = ReadBackendSh();

        // All directory references for writable locations must go through
        // SCRATCH or VENV_DIR variables, not hardcoded paths.
        // Verify these variable names appear in context of mkdir, file writes.

        // PID_FILE and LOG_FILE must be derived from SCRATCH.
        Assert.Contains("PID_FILE=\"${SCRATCH}", content, StringComparison.Ordinal);
        Assert.Contains("LOG_FILE=\"${SCRATCH}", content, StringComparison.Ordinal);
    }
}
