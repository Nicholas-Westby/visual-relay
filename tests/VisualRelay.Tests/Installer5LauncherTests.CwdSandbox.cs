namespace VisualRelay.Tests;

public sealed partial class Installer5LauncherTests
{
    // ── 6. Cwd-independent dev dispatch ─────────────────────────────────
    //
    // The per-command init dispatch (absolute --project path, ORIGINAL_CWD
    // forwarding, explicit-path precedence) moved into VisualRelay.Cli's init
    // command and is covered by CliInitCommandTests. The bootstrap-level property
    // below (nix re-entry forwards ORIGINAL_CWD) stays against the thin bash. The
    // sandbox is always on with no opt-out, so the launcher carries no bypass read;
    // the stale-key regression is CliNonoGateTests.Launch_StaleBypassSandboxKey_StillRequiresNono.

    /// <summary>When nix re-entry execs, $ORIGINAL_CWD must be forwarded
    /// through the environment so the re-entered launcher can forward it to
    /// cwd-sensitive subcommands (init).</summary>
    [Fact]
    public async Task NixReentry_PreservesOriginalCwd()
    {
        var testBody = """
            TEST_DIR=$(mktemp -d); STUB_DIR="$TEST_DIR/bin"
            NIX_CWD_LOG="/tmp/.vr-test-nix-cwd"
            trap 'rm -rf "$TEST_DIR" "$NIX_CWD_LOG"' EXIT
            rm -f "$NIX_CWD_LOG"

            mkdir -p "$STUB_DIR"
            cat > "$STUB_DIR/nix" << 'X' && chmod +x "$STUB_DIR/nix"
            #!/bin/bash
            echo "$ORIGINAL_CWD" >> /tmp/.vr-test-nix-cwd
            exit 0
            X

            cd "$TEST_DIR"
            # No dotnet on PATH — forces _require_dotnet to fall through to nix
            PATH="$STUB_DIR:/usr/bin:/bin" VISUAL_RELAY_NIX_REENTRY= ORIGINAL_CWD= bash "$LAUNCHER" init 2>/dev/null || true

            if [[ ! -f "$NIX_CWD_LOG" ]]; then
                echo "FAIL: nix stub was not invoked (maybe real dotnet on PATH?)" >&2
                exit 1
            fi
            LOGGED_CWD=$(head -1 "$NIX_CWD_LOG")
            if [[ "$LOGGED_CWD" != "$TEST_DIR" ]]; then
                echo "FAIL: nix env ORIGINAL_CWD='$LOGGED_CWD' != '$TEST_DIR'" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunLauncherTestAsync("nix-reentry-cwd", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }
}
