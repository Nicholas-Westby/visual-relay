namespace VisualRelay.Tests;

/// <summary>
/// Hermetic runtime tests for the gen-backend-config timeout guard in
/// backend.sh.  Each test copies backend.sh into an isolated temp repo
/// with stubbed dependencies so no real litellm is launched.
/// These must FAIL before the implementation lands.
/// </summary>
public sealed partial class Installer5BackendShTests
{
    // ── 1. Timeout: mock gen-backend-config hangs ────────────────────────

    /// <summary>
    /// When visual-relay gen-backend-config sleeps beyond GEN_CONFIG_TIMEOUT,
    /// start must complete (not hang), fall back to static config, and log
    /// the timeout distinctly from a plain non-zero exit.
    /// </summary>
    [Fact]
    public async Task GenConfigTimeout_TimedOut_FallsBackToStaticConfig()
    {
        var testBody = """
            TEST_HOME=$(mktemp -d)
            STUB_DIR="$TEST_HOME/bin"
            FAKE_REPO="$TEST_HOME/repo"
            trap 'rm -rf "$TEST_HOME"' EXIT

            mkdir -p "$FAKE_REPO/tools/backend"
            cp "$BACKEND_SH" "$FAKE_REPO/tools/backend/backend.sh"
            mkdir -p "$STUB_DIR"

            # Stub curl: always unhealthy so start does not short-circuit.
            cat > "$STUB_DIR/curl" << 'X' && chmod +x "$STUB_DIR/curl"
            #!/bin/bash
            exit 7
            X

            # Stub litellm: record that it was invoked, then exit so the
            # readiness poll fails fast (no-op proxy).
            cat > "$STUB_DIR/litellm" << 'X' && chmod +x "$STUB_DIR/litellm"
            #!/bin/bash
            echo "litellm-stub ran" >> "$TEST_HOME/litellm-invoked"
            exit 0
            X

            # Stub visual-relay: gen-backend-config sleeps beyond the timeout
            # so the watchdog / timeout command kills it.
            cat > "$FAKE_REPO/visual-relay" << 'X' && chmod +x "$FAKE_REPO/visual-relay"
            #!/bin/bash
            if [[ "${1:-}" == gen-backend-config ]]; then
                sleep 30
                exit 0
            fi
            exit 1
            X

            export XDG_DATA_HOME="$TEST_HOME/.local/share"
            export HOME="$TEST_HOME"
            START_STDERR="$TEST_HOME/start-stderr"
            START_TIME=$(date +%s)
            PATH="$STUB_DIR:/usr/bin:/bin" \
                VISUAL_RELAY_GEN_CONFIG_TIMEOUT=2 \
                VISUAL_RELAY_BACKEND_TIMEOUT=1 \
                bash "$FAKE_REPO/tools/backend/backend.sh" start >/dev/null 2>"$START_STDERR" || true
            ELAPSED=$(( $(date +%s) - START_TIME ))

            # Assert: the script completed quickly (did not hang for the full
            # 30 s mock sleep).  Allow generous overhead but well below 30.
            if (( ELAPSED > 15 )); then
                echo "FAIL: start hung for ${ELAPSED}s (expected <15s)" >&2
                exit 1
            fi

            # Assert: stderr mentions a timeout (not just "unavailable").
            if ! grep -q 'timed out' "$START_STDERR"; then
                echo "FAIL: no timeout message in stderr" >&2
                echo "=== stderr ===" >&2
                cat "$START_STDERR" >&2
                exit 1
            fi

            # Assert: stderr confirms fallback to static config.
            if ! grep -q 'using static config' "$START_STDERR"; then
                echo "FAIL: no 'using static config' log" >&2
                echo "=== stderr ===" >&2
                cat "$START_STDERR" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunBackendShTestAsync("gen-config-timeout", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    // ── 2. Non-zero exit: existing graceful-degradation path ─────────────

    /// <summary>
    /// When visual-relay gen-backend-config exits non-zero, start falls back
    /// to static config and logs the unavailability.  This is the existing
    /// path — the timeout fix must preserve it unchanged.
    /// </summary>
    [Fact]
    public async Task GenConfigTimeout_NonZeroExit_FallsBackToStaticConfig()
    {
        var testBody = """
            TEST_HOME=$(mktemp -d)
            STUB_DIR="$TEST_HOME/bin"
            FAKE_REPO="$TEST_HOME/repo"
            trap 'rm -rf "$TEST_HOME"' EXIT

            mkdir -p "$FAKE_REPO/tools/backend"
            cp "$BACKEND_SH" "$FAKE_REPO/tools/backend/backend.sh"
            mkdir -p "$STUB_DIR"

            # Stub curl: always unhealthy.
            cat > "$STUB_DIR/curl" << 'X' && chmod +x "$STUB_DIR/curl"
            #!/bin/bash
            exit 7
            X

            # Stub litellm: exit so readiness poll fails fast.
            cat > "$STUB_DIR/litellm" << 'X' && chmod +x "$STUB_DIR/litellm"
            #!/bin/bash
            exit 0
            X

            # Stub visual-relay: gen-backend-config exits 1 immediately.
            cat > "$FAKE_REPO/visual-relay" << 'X' && chmod +x "$FAKE_REPO/visual-relay"
            #!/bin/bash
            if [[ "${1:-}" == gen-backend-config ]]; then
                echo "mock gen-config error" >&2
                exit 1
            fi
            exit 1
            X

            export XDG_DATA_HOME="$TEST_HOME/.local/share"
            export HOME="$TEST_HOME"
            START_STDERR="$TEST_HOME/start-stderr"
            PATH="$STUB_DIR:/usr/bin:/bin" \
                VISUAL_RELAY_BACKEND_TIMEOUT=1 \
                bash "$FAKE_REPO/tools/backend/backend.sh" start >/dev/null 2>"$START_STDERR" || true

            # Assert: the existing graceful-degradation log line appears.
            if ! grep -q 'gen-backend-config unavailable' "$START_STDERR"; then
                echo "FAIL: no 'gen-backend-config unavailable' log" >&2
                echo "=== stderr ===" >&2
                cat "$START_STDERR" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunBackendShTestAsync("gen-config-nonzero", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    // ── 3. Success: generated config is produced and used ────────────────

    /// <summary>
    /// When visual-relay gen-backend-config succeeds within the timeout,
    /// start writes the generated config to the scratch directory.
    /// </summary>
    [Fact]
    public async Task GenConfigTimeout_Success_UsesGeneratedConfig()
    {
        var testBody = """
            TEST_HOME=$(mktemp -d)
            STUB_DIR="$TEST_HOME/bin"
            FAKE_REPO="$TEST_HOME/repo"
            trap 'rm -rf "$TEST_HOME"' EXIT

            mkdir -p "$FAKE_REPO/tools/backend"
            cp "$BACKEND_SH" "$FAKE_REPO/tools/backend/backend.sh"
            mkdir -p "$STUB_DIR"

            # Stub curl: always unhealthy.
            cat > "$STUB_DIR/curl" << 'X' && chmod +x "$STUB_DIR/curl"
            #!/bin/bash
            exit 7
            X

            # Stub litellm: exit so readiness poll fails fast.
            cat > "$STUB_DIR/litellm" << 'X' && chmod +x "$STUB_DIR/litellm"
            #!/bin/bash
            exit 0
            X

            # Stub visual-relay: gen-backend-config succeeds and writes a
            # recognizable YAML snippet to stdout.
            cat > "$FAKE_REPO/visual-relay" << 'X' && chmod +x "$FAKE_REPO/visual-relay"
            #!/bin/bash
            if [[ "${1:-}" == gen-backend-config ]]; then
                printf 'model_list:\n  - model_name: stub-model\n'
                exit 0
            fi
            exit 1
            X

            export XDG_DATA_HOME="$TEST_HOME/.local/share"
            export HOME="$TEST_HOME"
            START_STDERR="$TEST_HOME/start-stderr"
            GEN_FILE="$XDG_DATA_HOME/visual-relay/scratch/litellm-config.generated.yaml"
            PATH="$STUB_DIR:/usr/bin:/bin" \
                VISUAL_RELAY_BACKEND_TIMEOUT=1 \
                bash "$FAKE_REPO/tools/backend/backend.sh" start >/dev/null 2>"$START_STDERR" || true

            # Assert: the generated config file was created by the redirect.
            if [[ ! -f "$GEN_FILE" ]]; then
                echo "FAIL: generated config not created at $GEN_FILE" >&2
                echo "=== stderr ===" >&2
                cat "$START_STDERR" >&2
                exit 1
            fi

            # Assert: the content matches what the stub wrote to stdout.
            if ! grep -q 'stub-model' "$GEN_FILE"; then
                echo "FAIL: generated config missing expected content" >&2
                echo "=== generated config ===" >&2
                cat "$GEN_FILE" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunBackendShTestAsync("gen-config-success", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }
}
