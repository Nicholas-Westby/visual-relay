namespace VisualRelay.Tests;

public sealed partial class Installer5LauncherTests
{
    // ── 6. Cwd-independent dev dispatch ─────────────────────────────────

    /// <summary>When the launcher is invoked from outside the repo root, every
    /// dotnet-run --project path must be absolute (anchored to $SCRIPT_DIR).
    /// This test stubs dotnet, runs init from a temp directory, and asserts the
    /// --project argument starts with '/'.</summary>
    [Fact]
    public async Task DevDispatch_InitProjectPathIsAbsolute()
    {
        var testBody = """
            TEST_DIR=$(mktemp -d); STUB_DIR="$TEST_DIR/bin"
            DOTNET_LOG="/tmp/.vr-test-dotnet-argv"
            trap 'rm -rf "$TEST_DIR" "$DOTNET_LOG"' EXIT
            rm -f "$DOTNET_LOG"

            # Stub dotnet: logs full argv (one arg per line), exits 0
            mkdir -p "$STUB_DIR"
            cat > "$STUB_DIR/dotnet" << 'X' && chmod +x "$STUB_DIR/dotnet"
            #!/bin/bash
            printf '%s\n' "$@" >> /tmp/.vr-test-dotnet-argv
            exit 0
            X

            cd "$TEST_DIR"
            VISUAL_RELAY_NIX_REENTRY=1 PATH="$STUB_DIR:/usr/bin:/bin" bash "$LAUNCHER" init 2>/dev/null || true

            # Find the --project argument value (line after --project)
            PROJECT_VAL=$(awk '/^--project$/ { getline; print; exit }' "$DOTNET_LOG")
            if [[ -z "$PROJECT_VAL" ]]; then
                echo "FAIL: --project not found in dotnet argv" >&2
                cat "$DOTNET_LOG" >&2
                exit 1
            fi
            if [[ "$PROJECT_VAL" != /* ]]; then
                echo "FAIL: --project value is not absolute: '$PROJECT_VAL'" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunLauncherTestAsync("init-project-abs", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    /// <summary>When init is called with no explicit path, the launcher must
    /// forward $ORIGINAL_CWD (the caller's original working directory) to the
    /// Init tool so it initializes the correct repo.</summary>
    [Fact]
    public async Task Init_ForwardsOriginalCwdWhenNoArgs()
    {
        var testBody = """
            TEST_DIR=$(mktemp -d); STUB_DIR="$TEST_DIR/bin"
            DOTNET_LOG="/tmp/.vr-test-dotnet-argv"
            trap 'rm -rf "$TEST_DIR" "$DOTNET_LOG"' EXIT
            rm -f "$DOTNET_LOG"

            mkdir -p "$STUB_DIR"
            cat > "$STUB_DIR/dotnet" << 'X' && chmod +x "$STUB_DIR/dotnet"
            #!/bin/bash
            printf '%s\n' "$@" >> /tmp/.vr-test-dotnet-argv
            exit 0
            X

            cd "$TEST_DIR"
            VISUAL_RELAY_NIX_REENTRY=1 ORIGINAL_CWD="$TEST_DIR" PATH="$STUB_DIR:/usr/bin:/bin" bash "$LAUNCHER" init 2>/dev/null || true

            # The last argument after '--' should be the original cwd (temp dir)
            ROOT_ARG=$(awk '/^--$/ { getline; print; exit }' "$DOTNET_LOG")
            if [[ -z "$ROOT_ARG" ]]; then
                echo "FAIL: no root path argument found after -- in dotnet argv" >&2
                cat "$DOTNET_LOG" >&2
                exit 1
            fi
            if [[ "$ROOT_ARG" != "$TEST_DIR" ]]; then
                echo "FAIL: root arg '$ROOT_ARG' != TEST_DIR '$TEST_DIR'" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunLauncherTestAsync("init-forward-cwd", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    /// <summary>When the user supplies an explicit path to init, that path
    /// must take precedence over $ORIGINAL_CWD.</summary>
    [Fact]
    public async Task Init_PassesExplicitPathWhenGiven()
    {
        var testBody = """
            TEST_DIR=$(mktemp -d); STUB_DIR="$TEST_DIR/bin"
            EXPLICIT_PATH="/explicit/test/path/for/vr"
            DOTNET_LOG="/tmp/.vr-test-dotnet-argv"
            trap 'rm -rf "$TEST_DIR" "$DOTNET_LOG"' EXIT
            rm -f "$DOTNET_LOG"

            mkdir -p "$STUB_DIR"
            cat > "$STUB_DIR/dotnet" << 'X' && chmod +x "$STUB_DIR/dotnet"
            #!/bin/bash
            printf '%s\n' "$@" >> /tmp/.vr-test-dotnet-argv
            exit 0
            X

            cd "$TEST_DIR"
            VISUAL_RELAY_NIX_REENTRY=1 PATH="$STUB_DIR:/usr/bin:/bin" bash "$LAUNCHER" init "$EXPLICIT_PATH" 2>/dev/null || true

            ROOT_ARG=$(awk '/^--$/ { getline; print; exit }' "$DOTNET_LOG")
            if [[ "$ROOT_ARG" != "$EXPLICIT_PATH" ]]; then
                echo "FAIL: root arg '$ROOT_ARG' != '$EXPLICIT_PATH'" >&2
                cat "$DOTNET_LOG" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunLauncherTestAsync("init-explicit-path", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

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

    // ── 7. _read_bypass_sandbox reads config from SCRIPT_DIR, not cwd ───

    /// <summary>
    /// When the launcher is invoked from outside the repo root,
    /// _read_bypass_sandbox must read .relay/config.json from $SCRIPT_DIR
    /// (the repo root), NOT from the caller's cwd.  Before the fix the
    /// function used bare ".relay/config.json" which resolves against $PWD.
    ///
    /// This test creates a fake repo with bypassSandbox:true and the launcher,
    /// cds to a <em>different</em> directory, then invokes launch.  If the
    /// bypass is read from the wrong directory the sandbox stays enabled,
    /// _require_nono fires, nono is absent from the stubbed PATH, and the
    /// launcher exits 127 — dotnet is never called.
    /// </summary>
    [Fact]
    public async Task BypassSandbox_ReadsConfigFromScriptDir()
    {
        var testBody = """
            FAKE_REPO=$(mktemp -d)
            CALLER_DIR=$(mktemp -d)
            STUB_DIR="$FAKE_REPO/bin"
            DOTNET_LOG="/tmp/.vr-test-bypass-dotnet"
            trap 'rm -rf "$FAKE_REPO" "$CALLER_DIR" "$DOTNET_LOG"' EXIT
            rm -f "$DOTNET_LOG"

            # Copy launcher to fake repo — SCRIPT_DIR will resolve here
            cp "$LAUNCHER" "$FAKE_REPO/visual-relay"
            chmod +x "$FAKE_REPO/visual-relay"

            # Place .relay/config.json with bypassSandbox:true at the repo root
            mkdir -p "$FAKE_REPO/.relay"
            echo '{"bypassSandbox":true}' > "$FAKE_REPO/.relay/config.json"

            # Stub tools/backend/backend.sh so the launch preamble doesn't fail
            mkdir -p "$FAKE_REPO/tools/backend"
            cat > "$FAKE_REPO/tools/backend/backend.sh" << 'X' && chmod +x "$FAKE_REPO/tools/backend/backend.sh"
            #!/bin/bash
            exit 0
            X

            # Stub dotnet: logs full argv (one arg per line), exits 0
            mkdir -p "$STUB_DIR"
            cat > "$STUB_DIR/dotnet" << 'X' && chmod +x "$STUB_DIR/dotnet"
            #!/bin/bash
            printf '%s\n' "$@" >> /tmp/.vr-test-bypass-dotnet
            exit 0
            X

            # cd to a directory that is NOT the fake repo
            cd "$CALLER_DIR"
            PATH="$STUB_DIR:/usr/bin:/bin" VISUAL_RELAY_NIX_REENTRY=1 bash "$FAKE_REPO/visual-relay" launch 2>/dev/null || true

            # After the fix dotnet must have been called (bypass worked).
            # Before the fix _read_bypass_sandbox looks for .relay/config.json
            # in CALLER_DIR, fails to find it, enables sandbox, and
            # _require_nono exits 127 because nono is not on PATH.
            if [[ ! -f "$DOTNET_LOG" ]]; then
                echo "FAIL: dotnet was not called — bypassSandbox not read from SCRIPT_DIR?" >&2
                exit 1
            fi
            if ! grep -q '^--project$' "$DOTNET_LOG"; then
                echo "FAIL: dotnet not called with --project; bypass may have failed" >&2
                cat "$DOTNET_LOG" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunLauncherTestAsync("bypass-sandbox-scriptdir", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }
}
