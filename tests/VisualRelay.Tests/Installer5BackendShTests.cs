using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <c>tools/backend/backend.sh</c> changes in bootstrap-1:
/// unconditional XDG paths, execution probe, broken-venv self-heal,
/// and legacy repo-local state cleanup.
/// These must FAIL before the implementation lands.
/// </summary>
public sealed partial class Installer5BackendShTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string BackendShPath => Path.Combine(RepoRoot, "tools", "backend", "backend.sh");

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ReadBackendSh() =>
        File.ReadAllText(BackendShPath);

    /// <summary>Runs an embedded bash test script that sources or copies
    /// backend.sh in a controlled environment and returns
    /// (exitCode, stdout, stderr). Modeled on RunLauncherTestAsync.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunBackendShTestAsync(
        string testName, string testBody)
    {
        var script = Path.Combine(Path.GetTempPath(), $"vr-backend-test-{testName}.sh");
        var escapedBackendShPath = BackendShPath.Replace("'", "'\\''");
        var fullScript = $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            BACKEND_SH='{{escapedBackendShPath}}'
            {{testBody}}
            """;
        await File.WriteAllTextAsync(script, fullScript);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process? process = null;
        try
        {
            process = new Process();
            process.StartInfo = new ProcessStartInfo("/bin/bash", script)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.Start();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode,
                await process.StandardOutput.ReadToEndAsync(),
                await process.StandardError.ReadToEndAsync());
        }
        catch (OperationCanceledException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally { try { File.Delete(script); } catch { } }
    }

    // ── 1. Unconditional XDG paths ───────────────────────────────────────

    [Fact]
    public void BackendSh_UsesXdgDataHome_Unconditionally()
    {
        var content = ReadBackendSh();

        // DATA_HOME, VENV_DIR, and SCRATCH must be set to XDG paths
        // unconditionally — not inside a [[ -w ... ]] branch.
        Assert.Contains("DATA_HOME=\"${XDG_DATA_HOME:-$HOME/.local/share}/visual-relay\"",
            content, StringComparison.Ordinal);
        Assert.Contains("VENV_DIR=\"${DATA_HOME}/backend-venv\"",
            content, StringComparison.Ordinal);
        Assert.Contains("SCRATCH=\"${DATA_HOME}/scratch\"",
            content, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendSh_DoesNotCheckRepoRootWritability()
    {
        var content = ReadBackendSh();

        // The old [[ -w "${REPO_ROOT}" ]] branch must be gone.
        Assert.DoesNotContain("-w \"${REPO_ROOT}\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendSh_DoesNotReferToRelayScratchOrScriptDotVenv_AsPaths()
    {
        var content = ReadBackendSh();

        // No repo-local .relay-scratch or SCRIPT_DIR/.venv as active paths.
        // The only mention of .relay-scratch should be in the legacy cleanup
        // (removing old state) — not as SCRATCH or VENV_DIR assignments.
        Assert.DoesNotContain("SCRATCH=\"${REPO_ROOT}/.relay-scratch\"",
            content, StringComparison.Ordinal);
        Assert.DoesNotContain("VENV_DIR=\"${SCRIPT_DIR}/.venv\"",
            content, StringComparison.Ordinal);
    }

    // ── 2. Execution probe ───────────────────────────────────────────────

    [Fact]
    public void BackendSh_HasExecutionProbe()
    {
        var content = ReadBackendSh();

        // The ensure_litellm probe must execute the venv python to verify it
        // actually runs — catches dangling/foreign interpreter shebangs.
        Assert.Contains("\"${VENV_PY}\" -V", content, StringComparison.Ordinal);
        Assert.Contains("2>&1", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendSh_HasBrokenVenvRemoval()
    {
        var content = ReadBackendSh();

        // After a failed execution probe the script must remove the broken venv
        // so uv rebuilds from scratch.
        Assert.Contains("removing broken venv", content, StringComparison.Ordinal);
        Assert.Contains("rm -rf \"${VENV_DIR}\"", content, StringComparison.Ordinal);
    }

    // ── 3. Legacy cleanup ────────────────────────────────────────────────

    [Fact]
    public void BackendSh_HasLegacyCleanup()
    {
        var content = ReadBackendSh();

        // cmd_start must remove legacy repo-local .venv and .relay-scratch.
        Assert.Contains("removing legacy repo-local venv", content, StringComparison.Ordinal);
        Assert.Contains("removing legacy repo-local scratch", content, StringComparison.Ordinal);
    }

    // ── 4. Derived paths ─────────────────────────────────────────────────

    [Fact]
    public void BackendSh_DerivesPidAndLogFromScratch()
    {
        var content = ReadBackendSh();

        // PID_FILE and LOG_FILE must be derived from SCRATCH (now always XDG).
        Assert.Contains("PID_FILE=\"${SCRATCH}/litellm.pid\"",
            content, StringComparison.Ordinal);
        Assert.Contains("LOG_FILE=\"${SCRATCH}/litellm.log\"",
            content, StringComparison.Ordinal);
    }

    // ── 5. Gen-backend-config timeout ────────────────────────────────────

    [Fact]
    public void BackendSh_HasGenConfigTimeout()
    {
        var content = ReadBackendSh();

        // GEN_CONFIG_TIMEOUT must be defined with a default value so the
        // config-generation step is always bounded.
        Assert.Contains("GEN_CONFIG_TIMEOUT", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendSh_GenConfigTimeoutIsOverridable()
    {
        var content = ReadBackendSh();

        // The timeout must be overridable via VISUAL_RELAY_GEN_CONFIG_TIMEOUT
        // so callers (CI, operators) can tune it.
        Assert.Contains("VISUAL_RELAY_GEN_CONFIG_TIMEOUT", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendSh_HasTimeoutGuardForGenBackendConfig()
    {
        var content = ReadBackendSh();

        // The gen-backend-config invocation must be wrapped with some form of
        // timeout guard — either the GNU coreutils 'timeout' command or a
        // dedicated bash helper — so a wedged generator cannot hang startup.
        var hasTimeoutGuard =
            content.Contains("timeout \"${GEN_CONFIG_TIMEOUT}\"", StringComparison.Ordinal) ||
            content.Contains("_gen_config_with_timeout", StringComparison.Ordinal) ||
            content.Contains("timeout \"${GEN_CONFIG_TIMEOUT}\" \"${REPO_ROOT}", StringComparison.Ordinal);
        Assert.True(hasTimeoutGuard,
            "backend.sh must wrap gen-backend-config with a timeout guard (timeout command or _gen_config_with_timeout helper)");
    }

}
