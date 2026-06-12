namespace VisualRelay.Tests;

/// <summary>
/// Hermetic runtime tests for backend.sh: paths, self-heal, legacy cleanup.
/// Each test copies backend.sh into an isolated temp repo with stubbed
/// dependencies so no real litellm is launched and no real repo state is touched.
/// These must FAIL before the implementation lands.
/// </summary>
public sealed partial class Installer5BackendShTests
{
    // ── 1. Paths: unconditional XDG ──────────────────────────────────────

    /// <summary>With XDG_DATA_HOME pointed at a temp dir and a writable repo,
    /// start creates scratch under XDG and nothing under the repo.</summary>
    [Fact]
    public async Task Start_CreatesScratchInXdgDataHome_NotInRepo()
    {
        var testBody = """
            TEST_HOME=$(mktemp -d)
            STUB_DIR="$TEST_HOME/bin"
            FAKE_REPO="$TEST_HOME/repo"
            trap 'rm -rf "$TEST_HOME"' EXIT

            # Build a fake repo with backend.sh in the right relative location,
            # plus stubs for every external command backend.sh calls.
            mkdir -p "$FAKE_REPO/tools/backend"
            cp "$BACKEND_SH" "$FAKE_REPO/tools/backend/backend.sh"
            mkdir -p "$STUB_DIR"

            # Stub curl: always unhealthy so start does not short-circuit.
            cat > "$STUB_DIR/curl" << 'X' && chmod +x "$STUB_DIR/curl"
            #!/bin/bash
            exit 7
            X

            # Stub the visual-relay launcher so gen-backend-config exits 0.
            cat > "$FAKE_REPO/visual-relay" << 'X' && chmod +x "$FAKE_REPO/visual-relay"
            #!/bin/bash
            if [[ "${1:-}" == gen-backend-config ]]; then
                printf '# generated stub\n'
                exit 0
            fi
            exit 1
            X

            # No uv, no litellm on PATH — ensure_litellm fails harmlessly
            # after mkdir -p "${SCRATCH}" has already run.
            export XDG_DATA_HOME="$TEST_HOME/.local/share"
            export HOME="$TEST_HOME"
            PATH="$STUB_DIR:/usr/bin:/bin" \
                VISUAL_RELAY_BACKEND_TIMEOUT=1 \
                bash "$FAKE_REPO/tools/backend/backend.sh" start 2>/dev/null || true

            # Assert: scratch was created under XDG_DATA_HOME.
            if [[ ! -d "$XDG_DATA_HOME/visual-relay/scratch" ]]; then
                echo "FAIL: scratch not created under XDG_DATA_HOME" >&2
                echo "XDG_DATA_HOME=$XDG_DATA_HOME" >&2
                ls -la "$XDG_DATA_HOME/visual-relay/" 2>/dev/null || echo "(no visual-relay dir)" >&2
                exit 1
            fi

            # Assert: no .relay-scratch in the FAKE repo.
            if [[ -d "$FAKE_REPO/.relay-scratch" ]]; then
                echo "FAIL: .relay-scratch exists in repo (should not)" >&2
                exit 1
            fi

            # Assert: no tools/backend/.venv created in the FAKE repo.
            if [[ -d "$FAKE_REPO/tools/backend/.venv" ]]; then
                echo "FAIL: tools/backend/.venv exists in repo (should not)" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunBackendShTestAsync("paths-xdg", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    // ── 2. Self-heal: broken venv ────────────────────────────────────────

    /// <summary>Pre-create a venv whose bin/python is a dangling symlink
    /// (the exact 2026-06-11 failure shape) plus an executable bin/litellm.
    /// start must detect the broken venv via execution probe, remove it,
    /// and invoke uv to re-provision into the XDG path.</summary>
    [Fact]
    public async Task Start_SelfHealsBrokenVenv()
    {
        var testBody = """
            TEST_HOME=$(mktemp -d)
            STUB_DIR="$TEST_HOME/bin"
            FAKE_REPO="$TEST_HOME/repo"
            UV_LOG="$TEST_HOME/uv-argv"
            trap 'rm -rf "$TEST_HOME"' EXIT

            mkdir -p "$FAKE_REPO/tools/backend"
            cp "$BACKEND_SH" "$FAKE_REPO/tools/backend/backend.sh"
            mkdir -p "$STUB_DIR"

            # Stub uv: records argv, exits 0 but does NOT create a venv.
            cat > "$STUB_DIR/uv" << 'X' && chmod +x "$STUB_DIR/uv"
            #!/bin/bash
            printf '%s\n' "$@" >> "$UV_LOG"
            exit 0
            X

            # Stub curl: always unhealthy.
            cat > "$STUB_DIR/curl" << 'X' && chmod +x "$STUB_DIR/curl"
            #!/bin/bash
            exit 7
            X

            # Stub visual-relay launcher.
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
            BROKEN_VENV="$XDG_DATA_HOME/visual-relay/backend-venv"

            # Pre-create a broken venv at the XDG path: bin/python is a
            # dangling symlink, bin/litellm exists and is executable.
            # In the old code VENV_DIR is the repo path so this broken
            # venv is invisible; in the new code VENV_DIR is the XDG path
            # and the execution probe catches it.
            mkdir -p "$BROKEN_VENV/bin"
            ln -s "/nonexistent/python3" "$BROKEN_VENV/bin/python"
            cat > "$BROKEN_VENV/bin/litellm" << 'X' && chmod +x "$BROKEN_VENV/bin/litellm"
            #!/bin/bash
            echo "THIS SHOULD NOT RUN" >&2
            exit 1
            X

            export UV_LOG
            PATH="$STUB_DIR:/usr/bin:/bin" \
                VISUAL_RELAY_BACKEND_TIMEOUT=1 \
                bash "$FAKE_REPO/tools/backend/backend.sh" start 2>/dev/null || true

            # Assert: the broken venv at the XDG path was removed.
            # (Only the new execution-probe code does this; the old code
            #  uses a repo-local VENV_DIR and ignores this directory.)
            if [[ -d "$BROKEN_VENV" ]]; then
                echo "FAIL: broken venv at XDG path was not removed" >&2
                exit 1
            fi

            # Assert: uv was called to re-provision into the XDG venv.
            if [[ ! -f "$UV_LOG" ]]; then
                echo "FAIL: uv was not invoked" >&2
                exit 1
            fi
            if ! grep -qF "$BROKEN_VENV" "$UV_LOG"; then
                echo "FAIL: uv not called with XDG venv path" >&2
                echo "expected to find: $BROKEN_VENV" >&2
                echo "uv argv:" >&2
                cat "$UV_LOG" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunBackendShTestAsync("self-heal-venv", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }

    // ── 3. Legacy cleanup ────────────────────────────────────────────────

    /// <summary>Pre-create repo-local tools/backend/.venv/ and .relay-scratch/;
    /// start must remove both and log it.</summary>
    [Fact]
    public async Task Start_RemovesLegacyRepoLocalState()
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

            # Stub visual-relay launcher.
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

            # Pre-create legacy repo-local state (simulating old checkout).
            LEGACY_VENV="$FAKE_REPO/tools/backend/.venv"
            LEGACY_SCRATCH="$FAKE_REPO/.relay-scratch"
            mkdir -p "$LEGACY_VENV/bin"
            echo "legacy" > "$LEGACY_VENV/legacy.txt"
            mkdir -p "$LEGACY_SCRATCH"
            echo "legacy" > "$LEGACY_SCRATCH/legacy.txt"

            # Run start; capture stderr for log assertions.
            START_STDERR="$TEST_HOME/start-stderr"
            PATH="$STUB_DIR:/usr/bin:/bin" \
                VISUAL_RELAY_BACKEND_TIMEOUT=1 \
                bash "$FAKE_REPO/tools/backend/backend.sh" start >/dev/null 2>"$START_STDERR" || true

            # Assert: legacy repo-local state was removed.
            if [[ -d "$LEGACY_VENV" ]]; then
                echo "FAIL: legacy tools/backend/.venv not removed" >&2
                exit 1
            fi
            if [[ -d "$LEGACY_SCRATCH" ]]; then
                echo "FAIL: legacy .relay-scratch not removed" >&2
                exit 1
            fi

            # Assert: something was logged about removing legacy state.
            if ! grep -qi 'removing.*legacy\|legacy.*remov' "$START_STDERR"; then
                echo "FAIL: no log message about legacy removal" >&2
                echo "=== stderr ===" >&2
                cat "$START_STDERR" >&2
                exit 1
            fi
            """;
        var (exitCode, _, stderr) =
            await RunBackendShTestAsync("legacy-cleanup", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }
}
