# Harness: bound gen-backend-config with a timeout so a wedged generator cannot hang backend startup

`tools/backend/backend.sh` calls `"$REPO_ROOT/visual-relay" gen-backend-config` to produce
a key-aware LiteLLM config. The `visual-relay` launcher's `_ensure_devshell` re-execs into
`nix develop` for subcommands that require dotnet (task 06, always-enter-nix). On a backend
restart, this re-entry fires again; if nix is slow or wedged, the config-gen call blocks
indefinitely.

Observed 2026-06-14: a wedged `nix develop --command ... gen-backend-config` left
`backend.sh start` hanging with no output, no timeout, and the backend DOWN. All pipeline
runs were blocked until the hang was killed manually.

`backend.sh` already degrades gracefully when `gen-backend-config` exits non-zero (line
204–208: uses static config and logs "gen-backend-config unavailable"). A HANG never reaches
that branch.

## Current state (researched)

### The blocking call

`tools/backend/backend.sh:204`:
```bash
if "${REPO_ROOT}/visual-relay" gen-backend-config "${CONFIG}" >"${generated}" 2>/tmp/.vr-gen-stderr; then
    CONFIG="${generated}"
    [[ -s /tmp/.vr-gen-stderr ]] && log "$(head -n1 /tmp/.vr-gen-stderr)"
else
    log "gen-backend-config unavailable; using static config"
fi
```

This is a plain process substitution with no timeout. If `visual-relay gen-backend-config`
never returns (wedged nix shell, stalled dotnet, network hang during nix package fetch),
`cmd_start` hangs here forever.

### Existing timeout pattern in the launcher

`visual-relay:248–298`, `_timeout_watchdog`: a bash function that launches a command in a
child process group, runs a background sleep-timer, and kills the process group on expiry.
It uses `VISUAL_RELAY_TEST_TIMEOUT` as the timeout variable. The same pattern is reusable
for gen-backend-config with its own variable.

### Existing graceful-degradation for non-zero exit (lines 204–209)

When the `visual-relay gen-backend-config` command exits non-zero, `backend.sh` falls
through to the static config path. A timeout-triggered termination should produce a non-zero
exit from the process so the `if` condition fails and degradation fires automatically.

### Why nix re-entry happens here

`visual-relay:44–67`, `_ensure_devshell`: if dotnet is not on PATH, the launcher
re-execs the entire command inside `nix develop`. For `gen-backend-config`, this means
every backend restart triggers a nix develop entry. When nix is already evaluated and
cached, this is fast (~1 s). When nix is slow (first run, cache miss, nix daemon wedged),
it can stall indefinitely. A timeout bound is the correct primary fix — the nix re-entry
is by design, but it must not be allowed to hang startup.

### Optional: bypass nix re-entry for pure config-gen

When dotnet and the published gen-backend-config binary (`tools/gen-backend-config/...`)
are already on PATH (e.g. inside a nix shell), the nix re-entry in `_ensure_devshell`
buys nothing. The published binary check (`PUBLISHED_GC` at `visual-relay:25`,
`visual-relay:396–401`) already skips nix when the binary is available:

```bash
gen-backend-config)
    if [[ -x "$PUBLISHED_GC" ]]; then
        exec "$PUBLISHED_GC" "$@"
    fi
    _require_dotnet
    dotnet run ...
```

`_require_dotnet` (as opposed to `_ensure_devshell`) does NOT re-enter nix — it fails fast
if dotnet is absent. So nix re-entry only happens when `PUBLISHED_GC` is absent AND dotnet
is not on PATH. This is already a narrower case than it appears. The timeout fix is still
required as the primary guard for those cases.

## What to build

### 1. Add a configurable gen-config timeout to `backend.sh`

Near the existing `READY_TIMEOUT` and `STOP_GRACE` variables (lines 37–38):

```bash
GEN_CONFIG_TIMEOUT="${VISUAL_RELAY_GEN_CONFIG_TIMEOUT:-15}" # seconds
```

15 seconds is generous for a local dotnet `dotnet run` or published binary invocation;
it is a hard upper bound that prevents indefinite hangs. The variable is overridable by
callers who need more time.

### 2. Wrap the gen-backend-config call with a timeout

Replace the plain invocation at line 204 with a timeout-bounded equivalent. The simplest
general approach uses bash's `timeout` command (available on macOS via GNU coreutils or
the system `timeout`):

```bash
local generated="${SCRATCH}/litellm-config.generated.yaml"
if timeout "${GEN_CONFIG_TIMEOUT}" "${REPO_ROOT}/visual-relay" gen-backend-config "${CONFIG}" \
    >"${generated}" 2>/tmp/.vr-gen-stderr; then
    CONFIG="${generated}"
    [[ -s /tmp/.vr-gen-stderr ]] && log "$(head -n1 /tmp/.vr-gen-stderr)"
else
    local gen_rc=$?
    if (( gen_rc == 124 )); then
        log "gen-backend-config timed out after ${GEN_CONFIG_TIMEOUT}s; using static config"
    else
        log "gen-backend-config unavailable (exit ${gen_rc}); using static config"
    fi
fi
rm -f /tmp/.vr-gen-stderr
```

`timeout` exits 124 when the timeout fires (POSIX). The log distinguishes timeout from
failure so operators can diagnose whether the issue is a wedged nix/dotnet or a genuine
gen-config error.

If `timeout` is not available (some minimal macOS base installs lack GNU coreutils), fall
back to the `_timeout_watchdog` pattern from the launcher — a background sleep-and-kill
subshell. Add a helper `_gen_config_with_timeout` that detects `timeout` on PATH and falls
back to the watchdog pattern:

```bash
_gen_config_with_timeout() {
    local out_file="$1"; shift
    if command -v timeout >/dev/null 2>&1; then
        timeout "${GEN_CONFIG_TIMEOUT}" "${REPO_ROOT}/visual-relay" gen-backend-config "$@" \
            >"${out_file}" 2>/tmp/.vr-gen-stderr
        return $?
    fi
    # Fallback: background watchdog (mirrors _timeout_watchdog in visual-relay launcher).
    local cmd_pid rc=0
    "${REPO_ROOT}/visual-relay" gen-backend-config "$@" >"${out_file}" 2>/tmp/.vr-gen-stderr &
    cmd_pid=$!
    (
        sleep "${GEN_CONFIG_TIMEOUT}"
        kill -TERM "${cmd_pid}" 2>/dev/null || true
        sleep 1
        kill -KILL "${cmd_pid}" 2>/dev/null || true
    ) &
    local watchdog_pid=$!
    wait "${cmd_pid}" 2>/dev/null || rc=$?
    kill "${watchdog_pid}" 2>/dev/null || true
    wait "${watchdog_pid}" 2>/dev/null || true
    return $rc
}
```

### 3. Tests

Add to `tests/VisualRelay.Tests/Installer5BackendShTests.cs` (or a new partial file
`Installer5BackendShTests.GenConfigTimeout.cs`):

- `GenConfigTimeout_TimedOut_FallsBackToStaticConfig`: mock `visual-relay` script that
  sleeps beyond `GEN_CONFIG_TIMEOUT`; assert `backend.sh start` completes (does not hang),
  uses the static config (litellm started with the unmodified config path), and logs
  "gen-backend-config timed out".
- `GenConfigTimeout_NonZeroExit_FallsBackToStaticConfig`: mock `visual-relay` that exits 1
  immediately; assert existing graceful-degradation log "gen-backend-config unavailable"
  (confirms the non-timeout failure path is still covered).
- `GenConfigTimeout_Success_UsesGeneratedConfig`: mock `visual-relay` that writes a valid
  YAML within the timeout; assert `backend.sh start` uses the generated config.

These tests already have a precedent pattern in `Installer5BackendShTests.cs` and
`Installer5BackendShTests.Runtime.cs` — read those files before writing the new partial to
match their helper and fixture structure.

## Done when

- **Hang eliminated:** `VISUAL_RELAY_GEN_CONFIG_TIMEOUT=1 backend.sh start` with a mock
  `visual-relay gen-backend-config` that sleeps 10 s completes within ~3 s and starts
  litellm on the static config. Asserted by `GenConfigTimeout_TimedOut_FallsBackToStaticConfig`.
- **Timeout logged clearly:** the log line distinguishes timeout (exit 124 / watchdog kill)
  from error (other non-zero exit).
- **Graceful degradation preserved:** non-zero exit path unchanged; static config used;
  existing `GenConfigTimeout_NonZeroExit` test passes.
- **`GEN_CONFIG_TIMEOUT` overridable** via `VISUAL_RELAY_GEN_CONFIG_TIMEOUT` env var;
  default 15 seconds.
- **No machine-specific or nix-specific logic:** the fix is a simple timeout wrapper around
  a subprocess call. The `timeout`-vs-watchdog fallback handles environments where GNU
  coreutils is absent.
- **`./visual-relay check` green** after the change.
- **Files under 300 lines each:**
  - `tools/backend/backend.sh` (already 325 lines; the change adds ~20 lines; split or
    keep under 350 — verify the convention applies to shell scripts by checking existing guard;
    if the 300-line limit is .cs-only, note that in the commit)
  - `tests/VisualRelay.Tests/Installer5BackendShTests.GenConfigTimeout.cs` (new, <100 lines)
- **Conventional Commit subject candidates:**
  - `fix(backend): bound gen-backend-config with a timeout so a wedged generator cannot hang startup`
  - `fix(backend): timeout gen-backend-config call in backend.sh; degrade to static config on expiry`
