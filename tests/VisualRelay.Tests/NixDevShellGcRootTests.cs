using System.Diagnostics;
using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for pinning the nix devshell closure as a GC root via
/// <c>nix develop --profile</c> so <c>nix store gc</c> cannot strand a
/// running app. These must FAIL before the implementation lands (the bootstrap
/// currently has no <c>--profile</c>, no <c>wipe-history</c>, and no GC-root
/// comment).
/// </summary>
public sealed partial class NixDevShellGcRootTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string LauncherPath => Path.Combine(RepoRoot, "visual-relay");
    private static string ReadLauncher() => File.ReadAllText(LauncherPath);

    // ── Content assertions (red until implementation) ────────────────────

    /// <summary>
    /// The nix develop invocation in <c>_ensure_devshell</c> must include
    /// <c>--profile</c> so the devshell closure is registered as a GC root.
    /// Currently absent — this test MUST fail before the implementation.
    /// </summary>
    [Fact]
    public void NixDevelop_HasProfileFlag()
    {
        var content = ReadLauncher();
        // The --profile flag must appear between "develop" and "--command".
        Assert.Matches(@"develop\s+--profile\s+", content);
    }

    /// <summary>
    /// The profile path is derived from XDG_DATA_HOME (falling back to
    /// <c>$HOME/.local/share</c>) so it works on any machine without a
    /// hardcoded absolute path.
    /// </summary>
    [Fact]
    public void ProfilePath_IsDerivedFromXdgDataHome()
    {
        var content = ReadLauncher();
        Assert.Contains("XDG_DATA_HOME:-$HOME/.local/share}/visual-relay/nix-dev-profile",
            content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The parent directory of the profile must be created with
    /// <c>mkdir -p</c> before the <c>nix develop</c> call, otherwise
    /// nix will fail when the directory doesn't exist.
    /// </summary>
    [Fact]
    public void HasMkdirP_ForProfileParent()
    {
        var content = ReadLauncher();
        Assert.Matches(@"mkdir\s+-p.*dirname.*profile", content);
    }

    /// <summary>
    /// Before each entry, old profile generations are pruned via
    /// <c>nix profile wipe-history</c> (best-effort, guarded with
    /// <c>|| true</c>) so the profile directory doesn't grow forever.
    /// </summary>
    [Fact]
    public void HasProfileWipeHistory()
    {
        var content = ReadLauncher();
        Assert.Contains("profile wipe-history", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wipe-history call must be guarded with <c>|| true</c> so a
    /// failure (e.g. from an older nix version) never blocks launch.
    /// </summary>
    [Fact]
    public void WipeHistory_IsBestEffort()
    {
        var content = ReadLauncher();
        Assert.Matches(@"wipe-history.*\|\|\s*true", content);
    }

    /// <summary>
    /// A comment documents the accepted trade-off: a single shared profile
    /// protects only the most recently launched checkout when multiple
    /// checkouts exist on different flake.lock revisions.
    /// </summary>
    [Fact]
    public void HasComment_DocumentingSingleProfileTradeoff()
    {
        var content = ReadLauncher();
        Assert.Contains("single profile protects only the most recently launched",
            content, StringComparison.Ordinal);
    }

    // ── Size guard ───────────────────────────────────────────────────────

    /// <summary>
    /// The bootstrap must stay at or under the 100-logic-line ceiling after
    /// the new profile-pinning lines are added. This test gates the ceiling.
    /// Currently passes (~68 lines); must keep passing after implementation.
    /// </summary>
    [Fact]
    public void Bootstrap_IsWithin100LogicLineLimit()
    {
        var lines = File.ReadAllLines(LauncherPath);
        var count = ShellScriptLineCounter.CountLogicLines(lines);
        Assert.True(count <= ShellSizeGuard.BootstrapLimit,
            $"visual-relay has {count} logic lines, exceeding the {ShellSizeGuard.BootstrapLimit}-line ceiling. "
            + "Move new logic to C#, not the bootstrap.");
    }

    // ── End-to-end: stubbed nix receives --profile ───────────────────────

    /// <summary>
    /// When the launcher invokes nix develop, the stubbed nix binary must
    /// receive <c>--profile</c> with a path derived from XDG_DATA_HOME as
    /// one of its arguments. This is the integration-level proof that the
    /// profile flag is wired through.
    /// </summary>
    [Fact]
    public async Task StubNix_ReceivesProfileFlagInDevelopArgs()
    {
        var testBody = """
            STUB_DIR="/tmp/.vr-test-profile-stub-bin"
            NIX_ARGV_LOG="/tmp/.vr-test-profile-nix-argv"
            rm -rf "$STUB_DIR" "$NIX_ARGV_LOG"
            mkdir -p "$STUB_DIR"

            # Stub nix: logs all args and exits 0.
            cat > "$STUB_DIR/nix" << 'X' && chmod +x "$STUB_DIR/nix"
            #!/bin/bash
            printf '%s\n' "$@" >> /tmp/.vr-test-profile-nix-argv
            exit 0
            X

            # Set XDG_DATA_HOME to a known location so the profile path is
            # deterministic.
            XDG_DATA_HOME=/tmp/.vr-test-xdg-data \
                VISUAL_RELAY_NIX_REENTRY= \
                PATH="$STUB_DIR:/usr/bin:/bin" \
                bash "$LAUNCHER" gen-backend-config /dev/null 2>/dev/null || true

            # Assert --profile appears in the nix argv log.
            if ! grep -qFx -- '--profile' "$NIX_ARGV_LOG"; then
                echo "FAIL: --profile flag not found in nix argv" >&2
                echo "nix argv log:" >&2
                cat "$NIX_ARGV_LOG" >&2
                rm -rf "$STUB_DIR" "$NIX_ARGV_LOG"; exit 1
            fi

            # Assert the profile path uses the XDG_DATA_HOME prefix we set.
            if ! grep -q '/.vr-test-xdg-data/visual-relay/nix-dev-profile' "$NIX_ARGV_LOG"; then
                echo "FAIL: profile path not under expected XDG_DATA_HOME dir" >&2
                echo "nix argv log:" >&2
                cat "$NIX_ARGV_LOG" >&2
                rm -rf "$STUB_DIR" "$NIX_ARGV_LOG"; exit 1
            fi

            rm -rf "$STUB_DIR" "$NIX_ARGV_LOG"
            """;

        var (exitCode, _, stderr) =
            await RunLauncherTestAsync("profile-flag", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Stub-nix profile test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// The wipe-history call must NOT block launch when it fails (e.g. on an
    /// older nix without the <c>profile</c> subcommand). The stubbed nix
    /// returns non-zero for the wipe-history subcommand, and the launcher
    /// must still proceed to <c>nix develop</c>.
    /// </summary>
    [Fact]
    public async Task StubNix_WipeHistoryFailure_DoesNotBlockLaunch()
    {
        var testBody = """
            STUB_DIR="/tmp/.vr-test-wipefail-stub-bin"
            NIX_ARGV_LOG="/tmp/.vr-test-wipefail-nix-argv"
            rm -rf "$STUB_DIR" "$NIX_ARGV_LOG"
            mkdir -p "$STUB_DIR"

            # Stub nix: fails on "profile wipe-history" (no profile subcommand),
            # succeeds on "develop".
            cat > "$STUB_DIR/nix" << 'X' && chmod +x "$STUB_DIR/nix"
            #!/bin/bash
            printf '%s\n' "$@" >> /tmp/.vr-test-wipefail-nix-argv
            case "$*" in
                *profile*wipe-history*) exit 1 ;;
                *) exit 0 ;;
            esac
            X

            XDG_DATA_HOME=/tmp/.vr-test-xdg-data-wf \
                VISUAL_RELAY_NIX_REENTRY= \
                PATH="$STUB_DIR:/usr/bin:/bin" \
                bash "$LAUNCHER" gen-backend-config /dev/null 2>/dev/null || true

            # The launcher must NOT have exited early — the develop call must
            # be in the log.
            if ! grep -qFx -- '--command' "$NIX_ARGV_LOG"; then
                echo "FAIL: nix develop never reached after wipe-history failure" >&2
                echo "nix argv log:" >&2
                cat "$NIX_ARGV_LOG" >&2
                rm -rf "$STUB_DIR" "$NIX_ARGV_LOG"; exit 1
            fi

            rm -rf "$STUB_DIR" "$NIX_ARGV_LOG"
            """;

        var (exitCode, _, stderr) =
            await RunLauncherTestAsync("wipefail", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Stub-nix wipe-history failure test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    /// <summary>Runs an embedded bash script that sources the launcher's dispatch
    /// logic in a controlled environment and returns (exitCode, stdout, stderr).</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunLauncherTestAsync(
        string testName, string testBody)
    {
        var script = Path.Combine(Path.GetTempPath(), $"vr-nix-gcroot-test-{testName}.sh");
        var escapedLauncherPath = LauncherPath.Replace("'", "'\\''");
        var fullScript = $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            LAUNCHER='{{escapedLauncherPath}}'
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
                await process.StandardOutput.ReadToEndAsync(cts.Token),
                await process.StandardError.ReadToEndAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }
        finally { try { File.Delete(script); } catch (Exception) { } }
    }
}
