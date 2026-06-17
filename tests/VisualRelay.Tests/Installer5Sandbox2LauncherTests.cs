using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for sandbox-2: nono as a required launcher prerequisite, hard-error
/// guard, and idempotent pack/profile provisioning. These must FAIL before the
/// implementation lands — the launcher currently has no nono awareness at all.
/// </summary>
public sealed class Installer5Sandbox2LauncherTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string LauncherPath => Path.Combine(RepoRoot, "visual-relay");

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ReadLauncher() => File.ReadAllText(LauncherPath);

    /// <summary>Runs an embedded bash script and returns (exitCode, stdout, stderr).</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunBashTestAsync(
        string testName, string testBody)
    {
        var script = Path.Combine(Path.GetTempPath(), $"vr-s2-{testName}.sh");
        var escaped = LauncherPath.Replace("'", "'\\''");
        await File.WriteAllTextAsync(script,
            $"#!/usr/bin/env bash\nset -euo pipefail\nLAUNCHER='{escaped}'\n{testBody}\n");
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
                await process.StandardOutput.ReadToEndAsync(cts.Token),
                await process.StandardError.ReadToEndAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (Exception) { /* best-effort kill */ }
            throw;
        }
        finally { try { File.Delete(script); } catch (Exception) { /* best-effort temp cleanup */ } }
    }

    /// <summary>Generates bash snippet: sets up stubs + config, copies the
    /// launcher into the test sandbox so $SCRIPT_DIR resolves to $TEST_DIR,
    /// runs the launcher, then runs the provided bash assertions. Returns the
    /// full script body.</summary>
    private static string SetupRunAndAssert(bool bypassSandbox, bool stubNono,
        string? xdgRel, string assertions)
    {
        var nonoStub = stubNono
            ? "cat > \"$STUB_DIR/nono\" << 'X' && chmod +x \"$STUB_DIR/nono\"\n#!/bin/bash\nexit 0\nX"
            : "# nono intentionally absent";
        var xdgEnv = xdgRel is not null ? $"XDG_CONFIG_HOME=\"$TEST_DIR/{xdgRel}\"" : "";
        var bypass = bypassSandbox ? "true" : "false";
        return $$"""
            TEST_DIR=$(mktemp -d); STUB_DIR="$TEST_DIR/bin"
            mkdir -p "$STUB_DIR" "$TEST_DIR/.relay" "$TEST_DIR/tools/backend" "$TEST_DIR/packaging/nono"
            cat > "$STUB_DIR/dotnet" << 'X' && chmod +x "$STUB_DIR/dotnet"
            #!/bin/bash
            exit 0
            X
            # swival is a hard, always-required tool — stub it so launch reaches
            # the nono provisioning paths these tests assert on.
            cat > "$STUB_DIR/swival" << 'X' && chmod +x "$STUB_DIR/swival"
            #!/bin/bash
            exit 0
            X
            {{nonoStub}}
            cat > "$TEST_DIR/tools/backend/backend.sh" << 'X' && chmod +x "$TEST_DIR/tools/backend/backend.sh"
            #!/bin/bash
            exit 0
            X
            # Minimal vr-guard.json so _provision_nono has a source to install.
            echo '{"extends":"swival"}' > "$TEST_DIR/packaging/nono/vr-guard.json"
            echo '{"testCmd":"true","bypassSandbox":{{bypass}}}' > "$TEST_DIR/.relay/config.json"
            # Copy the launcher into the sandbox so SCRIPT_DIR == TEST_DIR.
            # _read_bypass_sandbox reads $SCRIPT_DIR/.relay/config.json, not cwd.
            cp "$LAUNCHER" "$TEST_DIR/visual-relay"
            chmod +x "$TEST_DIR/visual-relay"
            cd "$TEST_DIR"
            RC=0
            PATH="$STUB_DIR:/usr/bin:/bin" VISUAL_RELAY_NIX_REENTRY=1 {{xdgEnv}} \
                XDG_STATE_HOME="$TEST_DIR/state" VISUAL_RELAY_SWIVAL_LATEST_CMD=true \
                bash "$TEST_DIR/visual-relay" launch \
                >/tmp/.vr-s2-out 2>/tmp/.vr-s2-err || RC=$?
            echo "$RC" > /tmp/.vr-s2-rc
            {{assertions}}
            """;
    }

    // ── 1. Static analysis: launcher must contain nono-aware code ─────────

    [Fact]
    public void Launcher_ContainsRequireNonoFunction()
    {
        var content = ReadLauncher();
        Assert.Contains("_require_nono", content, StringComparison.Ordinal);
        Assert.Contains("command -v nono", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_ContainsReadBypassSandboxFunction()
    {
        var content = ReadLauncher();
        Assert.Contains("_read_bypass_sandbox", content, StringComparison.Ordinal);
        Assert.Contains("bypassSandbox", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_ContainsProvisionNonoFunction()
    {
        var content = ReadLauncher();
        Assert.Contains("_provision_nono", content, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchDispatch_CallsNonoFunctions()
    {
        var content = ReadLauncher();
        var launchCase = ExtractCaseBody(content, "launch|run");
        Assert.Contains("_require_nono", launchCase, StringComparison.Ordinal);
        Assert.Contains("_provision_nono", launchCase, StringComparison.Ordinal);
    }

    [Fact]
    public void RunTaskDispatch_CallsNonoGuard()
    {
        var content = ReadLauncher();
        var runTaskCase = ExtractCaseBody(content, "run-task)");
        Assert.Contains("_require_nono", runTaskCase, StringComparison.Ordinal);
    }

    // ── 2. Runtime: nono guard hard-error ────────────────────────────────

    [Fact]
    public async Task Launch_SandboxEnabled_NonoAbsent_ExitsNonZeroWithInstallMessage()
    {
        var testBody = SetupRunAndAssert(bypassSandbox: false, stubNono: false, xdgRel: null, """
            RC=$(cat /tmp/.vr-s2-rc)
            COMBINED=$(cat /tmp/.vr-s2-err /tmp/.vr-s2-out)
            if (( RC == 0 )); then
                echo "FAIL: should exit non-zero when nono absent and sandbox enabled" >&2
                echo "=== output ===" >&2; echo "$COMBINED" >&2
                rm -rf "$TEST_DIR" /tmp/.vr-s2-*; exit 1
            fi
            if ! echo "$COMBINED" | grep -qi "nono"; then
                echo "FAIL: output must mention nono" >&2
                echo "=== output ===" >&2; echo "$COMBINED" >&2
                rm -rf "$TEST_DIR" /tmp/.vr-s2-*; exit 1
            fi
            if ! echo "$COMBINED" | grep -qiE "install|brew|apt|nix"; then
                echo "FAIL: output must include install instructions" >&2
                echo "=== output ===" >&2; echo "$COMBINED" >&2
                rm -rf "$TEST_DIR" /tmp/.vr-s2-*; exit 1
            fi
            rm -rf "$TEST_DIR" /tmp/.vr-s2-*
            """);

        var (exitCode, _, stderr) = await RunBashTestAsync("no-nono-err", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Launch_BypassSandbox_NonoAbsent_ProceedsWithoutNonoError()
    {
        var testBody = SetupRunAndAssert(bypassSandbox: true, stubNono: false, xdgRel: null, """
            COMBINED=$(cat /tmp/.vr-s2-err /tmp/.vr-s2-out)
            if echo "$COMBINED" | grep -qi "nono"; then
                echo "FAIL: bypassSandbox:true should skip nono check" >&2
                echo "=== output ===" >&2; echo "$COMBINED" >&2
                rm -rf "$TEST_DIR" /tmp/.vr-s2-*; exit 1
            fi
            rm -rf "$TEST_DIR" /tmp/.vr-s2-*
            """);

        var (exitCode, _, stderr) = await RunBashTestAsync("bypass-ok", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Launch_SandboxEnabled_NonoPresent_ProceedsWithoutNonoError()
    {
        var testBody = SetupRunAndAssert(bypassSandbox: false, stubNono: true, xdgRel: "xdg", """
            COMBINED=$(cat /tmp/.vr-s2-err /tmp/.vr-s2-out)
            if echo "$COMBINED" | grep -qiE "nono.*not found|nono.*missing|nono.*install|nono.*required"; then
                echo "FAIL: nono present, should not produce nono-missing error" >&2
                echo "=== output ===" >&2; echo "$COMBINED" >&2
                rm -rf "$TEST_DIR" /tmp/.vr-s2-*; exit 1
            fi
            rm -rf "$TEST_DIR" /tmp/.vr-s2-*
            """);

        var (exitCode, _, stderr) = await RunBashTestAsync("nono-ok", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    // ── 3. Provisioning idempotence ──────────────────────────────────────

    [Fact]
    public async Task Provisioning_FirstRun_InstallsProfile()
    {
        var testBody = SetupRunAndAssert(bypassSandbox: false, stubNono: true, xdgRel: "xdg", """
            PROFILE="$TEST_DIR/xdg/nono/profiles/vr-guard.json"
            if [[ ! -f "$PROFILE" ]]; then
                echo "FAIL: vr-guard.json not installed to $PROFILE" >&2
                cat /tmp/.vr-s2-err >&2
                rm -rf "$TEST_DIR" /tmp/.vr-s2-*; exit 1
            fi
            rm -rf "$TEST_DIR" /tmp/.vr-s2-*
            """);

        var (exitCode, _, stderr) = await RunBashTestAsync("prov-install", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Provisioning_FirstRun_ProfileHasExpectedContent()
    {
        var testBody = SetupRunAndAssert(bypassSandbox: false, stubNono: true, xdgRel: "xdg", """
            PROFILE_DIR="$TEST_DIR/xdg/nono/profiles"
            PROFILE="$PROFILE_DIR/vr-guard.json"
            if [[ ! -f "$PROFILE" ]]; then
                echo "FAIL: vr-guard.json not installed on first run" >&2
                rm -rf "$TEST_DIR" /tmp/.vr-s2-*; exit 1
            fi
            if ! grep -q '"extends"' "$PROFILE"; then
                echo "FAIL: installed profile missing expected content" >&2
                echo "=== content ===" >&2; cat "$PROFILE" >&2
                rm -rf "$TEST_DIR" /tmp/.vr-s2-*; exit 1
            fi
            rm -rf "$TEST_DIR" /tmp/.vr-s2-*
            """);

        var (exitCode, _, stderr) = await RunBashTestAsync("prov-update", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Provisioning_BypassSandbox_SkipsProvisioning()
    {
        var testBody = SetupRunAndAssert(bypassSandbox: true, stubNono: false, xdgRel: "xdg", """
            PROFILE="$TEST_DIR/xdg/nono/profiles/vr-guard.json"
            if [[ -f "$PROFILE" ]]; then
                echo "FAIL: profile installed despite bypassSandbox:true" >&2
                rm -rf "$TEST_DIR" /tmp/.vr-s2-*; exit 1
            fi
            rm -rf "$TEST_DIR" /tmp/.vr-s2-*
            """);

        var (exitCode, _, stderr) = await RunBashTestAsync("prov-bypass", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the body of a case branch from the launcher script
    /// (caseLabel through the next ;; terminator).
    /// </summary>
    private static string ExtractCaseBody(string content, string caseLabel)
    {
        var startIdx = content.IndexOf(caseLabel, StringComparison.Ordinal);
        if (startIdx < 0) return string.Empty;
        var bodyStart = content.IndexOf('\n', startIdx) + 1;
        var terminator = content.IndexOf(";;", bodyStart, StringComparison.Ordinal);
        return terminator >= 0 ? content[bodyStart..terminator] : content[bodyStart..];
    }
}
