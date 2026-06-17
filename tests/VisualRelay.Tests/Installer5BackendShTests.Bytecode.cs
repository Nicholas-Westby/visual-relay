namespace VisualRelay.Tests;

/// <summary>
/// Runtime test for backend.sh bytecode suppression: the uv/litellm Python the
/// backend launches must run with PYTHONDONTWRITEBYTECODE=1 so it never writes
/// __pycache__/*.pyc back into its (often system-owned, e.g. Homebrew) stdlib
/// dir. The backend runs OUTSIDE nono, so this is not the sandbox-denial prompt,
/// but it is the same latent system-dir pollution the nono-wrapped swival/verify
/// paths suppress (see SwivalSubagentRunner.BuildSandboxEnvironment) — kept in
/// lockstep. Hermetic: stubs curl/uv/litellm so no real proxy is launched.
/// </summary>
public sealed partial class Installer5BackendShTests
{
    [Fact]
    public async Task Start_ExportsPythonDontWriteBytecode_ForSpawnedLitellm()
    {
        var testBody = """
            TEST_HOME=$(mktemp -d)
            STUB_DIR="$TEST_HOME/bin"
            FAKE_REPO="$TEST_HOME/repo"
            ENV_LOG="$TEST_HOME/litellm-env"
            trap 'rm -rf "$TEST_HOME"' EXIT

            mkdir -p "$FAKE_REPO/tools/backend"
            cp "$BACKEND_SH" "$FAKE_REPO/tools/backend/backend.sh"
            mkdir -p "$STUB_DIR"

            # Stub curl: always unhealthy so start does NOT short-circuit and
            # actually reaches the litellm launch (and never reports ready).
            cat > "$STUB_DIR/curl" << 'X' && chmod +x "$STUB_DIR/curl"
            #!/bin/bash
            exit 7
            X

            # Stub visual-relay launcher so gen-backend-config exits 0.
            cat > "$FAKE_REPO/visual-relay" << 'X' && chmod +x "$FAKE_REPO/visual-relay"
            #!/bin/bash
            if [[ "${1:-}" == gen-backend-config ]]; then
                printf '# generated stub\n'
                exit 0
            fi
            exit 1
            X

            export XDG_DATA_HOME="$TEST_HOME/.local/share"
            export HOME="$TEST_HOME"
            VENV="$XDG_DATA_HOME/visual-relay/backend-venv"

            # Pre-create a WORKING venv at the XDG path so ensure_litellm's probe
            # passes (real python for `python -V`, executable litellm stub) and the
            # script proceeds to launch litellm.
            mkdir -p "$VENV/bin"
            ln -s "$(command -v python3 || command -v python)" "$VENV/bin/python"
            # litellm stub: record its environment, then exit so the poll loop ends.
            cat > "$VENV/bin/litellm" << X && chmod +x "$VENV/bin/litellm"
            #!/bin/bash
            env > "$ENV_LOG"
            exit 0
            X

            PATH="$STUB_DIR:/usr/bin:/bin" \
                VISUAL_RELAY_BACKEND_TIMEOUT=1 \
                bash "$FAKE_REPO/tools/backend/backend.sh" start >/dev/null 2>&1 || true

            # Assert: the launched litellm saw PYTHONDONTWRITEBYTECODE=1.
            if [[ ! -f "$ENV_LOG" ]]; then
                echo "FAIL: litellm stub never ran (no env captured)" >&2
                exit 1
            fi
            if ! grep -qx 'PYTHONDONTWRITEBYTECODE=1' "$ENV_LOG"; then
                echo "FAIL: PYTHONDONTWRITEBYTECODE=1 not in litellm env" >&2
                echo "=== captured env (PYTHON*) ===" >&2
                grep '^PYTHON' "$ENV_LOG" >&2 || echo "(none)" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunBackendShTestAsync("pyc-suppress", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }
}
