## Stage 1 - Ideate

{
  "summary": "The backend.sh startup harness calls `visual-relay gen-backend-config` with no timeout guard, so if the dotnet command hangs (restore stall, compiler deadlock, FS hang) the backend never boots — the READY_TIMEOUT polling and the static-config fallback are both unreachable. Add a timeout around the config-generation step so a hung generator degrades to the static config and backend startup completes.",
  "options": [
    "Add a shell-level timeout wrapper in backend.sh around the gen-backend-config call, similar to the existing `_timeout_watchdog` in visual-relay (using `timeout` command or a background-kill pattern). On expiry, log the event and fall through to the static config path that already exists on line 208.",
    "Move the timeout into the `gen-backend-config` subcommand itself: pass a `--timeout` flag (default ~30s) and have the C# entry point enforce it via a CancellationTokenSource, exiting non-zero on expiry so the existing fallback in backend.sh (line 206-209) fires naturally.",
    "Add a `GEN_BACKEND_CONFIG_TIMEOUT` env variable defaulting to 30s, wrapped in a reusable shell helper in backend.sh (`_timeout_cmd <secs> <cmd...>`) that kills the process group after the budget. Reuse the helper for the READY_TIMEOUT polling loop too, giving a consistent pattern for all bounded operations in backend.sh."
  ]
}

## Stage 2 - Research

{
  "findings": "The `backend.sh` startup harness (line 204) invokes `\"${REPO_ROOT}/visual-relay\" gen-backend-config \"${CONFIG}\"` with **no timeout guard**. If the command hangs — whether from dotnet restore stall, compiler deadlock, filesystem hang, or a wedged published binary — the entire `cmd_start` function blocks indefinitely. The existing fallback path at lines 206-209 (\"gen-backend-config unavailable; using static config\") only fires on **non-zero exit**, never on a hang. The `READY_TIMEOUT` polling loop (lines 227-242) is unreachable until config generation completes.\n\nThe `visual-relay` script already has a battle-tested `_timeout_watchdog` function (lines 248-306) used for `test` and `check` commands — it backgrounds the command in its own process group, runs a timer subshell that kills the group on expiry, and uses a flag file to force exit code 124 (GNU timeout convention). However, this function lives in `visual-relay`, not in `backend.sh`. The two scripts are separate: `backend.sh` is a standalone lifecycle manager called from `visual-relay launch` (line 325).\n\nThe C# `GenBackendConfig/Program.cs` is entirely synchronous (no CancellationToken, no timeout argument). It reads a template file via `File.ReadAllLines`, reads env keys via `KeyEnvFile.Read()` (file I/O only, no network), generates YAML via `BackendConfigGenerator.Generate()`, and prints to stdout/stderr. The generation itself is pure CPU/string work — a hang would come from `dotnet run` infrastructure (restore/compilation) or the filesystem, not from the generated code.\n\nThere is already a `VISUAL_RELAY_BACKEND_TIMEOUT` env var (default 30s, line 37) for the readiness polling loop, but no equivalent for config generation. The published binary path (`PUBLISHED_GC`) uses `exec` directly, while dev builds use `dotnet run` — both paths need the timeout wrapper.",
  "constraints": [
    "`backend.sh` uses `set -euo pipefail` — any timeout solution must play well with errexit (e.g., `|| true` guards or subshell isolation).",
    "The GNU `timeout` command is not guaranteed to exist on macOS (requires `brew install coreutils` for `gtimeout`). The existing `_timeout_watchdog` in `visual-relay` avoids this dependency by using a pure-bash background-kill pattern with `set -m`, `pkill`, and process-group signaling.",
    "The `_timeout_watchdog` function from `visual-relay` is not available in `backend.sh` — they are separate scripts with no shared library/sourcing. Any reusable helper must be defined within `backend.sh` or sourced from a shared file.",
    "`gen-backend-config` can execute via two paths: (a) a pre-built published binary (`PUBLISHED_GC`, line 397) via `exec`, or (b) `dotnet run --project ...` (line 401). The timeout must cover both.",
    "The generated config output is captured via stdout redirection (`>\"${generated}\"`) and stderr to a temp file (`2>/tmp/.vr-gen-stderr`). A timeout wrapper must preserve or handle these redirects correctly so the fallback path still works.",
    "On timeout expiry, the fallback must use the static config at `$CONFIG` (which remains the original `litellm-config.yaml` template path — line 28) without a generated config replacing it.",
    "There is no existing `GEN_BACKEND_CONFIG_TIMEOUT` env variable; a new one would need to be defined with a sensible default (~30s) and documented alongside `VISUAL_RELAY_BACKEND_TIMEOUT`.",
    "The `BackendConfigGenerator.Generate()` method is a pure static function (no async, no CancellationToken) — adding a `--timeout` flag to the C# entry point would require wrapping the synchronous call in a Task with CancellationTokenSource, exiting non-zero on expiry so backend.sh's existing fallback fires naturally.",
    "`backend.sh` does not currently source any shared shell library — all helpers are defined inline. A reusable `_timeout_cmd` helper would be added entirely within `backend.sh` (or a new shared file sourced by both scripts).",
    "The `READY_TIMEOUT` polling loop (lines 227-242) uses a `sleep 1` loop — if option 3 (reusable `_timeout_cmd` helper) is adopted, refactoring the polling loop to use it must not change its current behavior or make it less readable."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The `backend.sh` startup harness invokes `visual-relay gen-backend-config` at line 204 with NO timeout guard. The call is a direct synchronous shell invocation:\n\n```bash\nif \"${REPO_ROOT}/visual-relay\" gen-backend-config \"${CONFIG}\" >\"${generated}\" 2>/tmp/.vr-gen-stderr; then\n  CONFIG=\"${generated}\"\nelse\n  log \"gen-backend-config unavailable; using static config\"\nfi\n```\n\nThis is a **blocking call with no deadline**. If the dotnet process hangs — from a restore stall, compiler deadlock, filesystem hang, or a wedged published binary — `cmd_start` blocks forever. The `else` fallback branch (lines 207-209) only fires on a **non-zero exit code**, never on a hang. The `READY_TIMEOUT` readiness-polling loop (lines 227-242) sits AFTER the generation step at line 226 and is completely unreachable until generation returns.\n\nThe `visual-relay` script already has a battle-tested `_timeout_watchdog` function (lines 248-306) that uses a pure-bash background-kill pattern with `set -m`, flag-file, and process-group signaling — no dependency on GNU `timeout` (which is absent on macOS). This watchdog wraps the `test` and `check` commands (lines 340, 390-391). However, it lives in `visual-relay`, not in `backend.sh` — the two scripts are separate, and `backend.sh` does not source `visual-relay`.\n\nThe `gen-backend-config` dispatch in `visual-relay` (lines 396-401) runs via two paths, neither with a timeout:\n  (a) Published binary: `exec \"$PUBLISHED_GC\" \"$@\"` (line 398)\n  (b) Dev build: `dotnet run --project ... -- \"$@\"` (line 401)\n\nThe C# entry point (`Program.cs`) is entirely synchronous: `File.ReadAllLines`, `KeyEnvFile.Read()`, `BackendConfigGenerator.Generate()`, then stdout/stderr writes. No `CancellationToken`, no `--timeout` argument. The `Generate()` method is pure CPU/string manipulation — a hang would originate in `dotnet run` infrastructure (restore locking, compiler stalls) or filesystem I/O, not in the generated code.\n\nThere is already a `VISUAL_RELAY_BACKEND_TIMEOUT` env var (default 30s, line 37) for the readiness polling loop, but no equivalent for config generation. The `backend.sh` script uses `set -euo pipefail` (line 12), so any timeout solution must handle errexit correctly (e.g., `|| true` guards or subshell isolation).",
  "excerpts": [
    "backend.sh:204-209 — gen-backend-config invocation with no timeout; only non-zero exit triggers fallback",
    "backend.sh:226-242 — READY_TIMEOUT polling loop sits after config gen; unreachable on hang",
    "backend.sh:37 — VISUAL_RELAY_BACKEND_TIMEOUT=30s exists for readiness but no GEN_BACKEND_CONFIG_TIMEOUT equivalent",
    "backend.sh:12 — `set -euo pipefail` constrains any timeout solution",
    "visual-relay:248-306 — battle-tested `_timeout_watchdog` function (flag-file, set -m, pkill process-group) lives here but is NOT available to backend.sh",
    "visual-relay:396-401 — gen-backend-config dispatch: either exec PUBLISHED_GC or dotnet run, neither with timeout",
    "Program.cs:1-28 — C# entry point: entirely synchronous, no CancellationToken, no --timeout flag",
    "BackendConfigGenerator.cs:82-192 — Generate() is pure CPU/string work; hang risk is in dotnet infrastructure, not here"
  ],
  "repro": "# Reproduce the hang risk:\n# In a dev checkout (no published binary), trigger a dotnet restore lock while\n# backend.sh tries to gen-backend-config. The cmd_start function will block\n# indefinitely at line 204 — the fallback is never reached.\n#\n# Simulated reproduction (does not actually hang, just demonstrates the gap):\ncd \"$REPO_ROOT\"\n# Observe that gen-backend-config runs with no timeout:\ngrep -n 'visual-relay.*gen-backend-config' tools/backend/backend.sh\n# Line 204:    if \"${REPO_ROOT}/visual-relay\" gen-backend-config \"${CONFIG}\" >\"${generated}\" 2>/tmp/.vr-gen-stderr; then\n# Compare with the timeout-guarded test command:\ngrep -n '_timeout_watchdog' visual-relay\n# Lines 340, 390-391: test and check are wrapped; gen-backend-config is not.\n#\n# The 'goto fail' consequence:\n# If gen-backend-config hangs, the backend never boots, and the only observable\n# symptom is a silent hang with no log message explaining why."
}

## Stage 4 - Plan

{
  "plan": "## Plan: Add timeout guard to gen-backend-config in backend.sh\n\n### Problem\n`backend.sh` line 204 calls `visual-relay gen-backend-config` with no timeout. If dotnet hangs (restore stall, compiler deadlock, FS hang), `cmd_start` blocks forever — the `READY_TIMEOUT` polling loop and static-config fallback are both unreachable.\n\n### Solution\nAdd a `_timeout_cmd` shell helper to `backend.sh` (same background-kill/flag-file pattern as the existing `_timeout_watchdog` in `visual-relay`) and wrap the gen-backend-config invocation with it. On expiry (exit 124), the existing `else` fallback branch fires: \"gen-backend-config unavailable; using static config\". A new `GEN_BACKEND_CONFIG_TIMEOUT` env var defaults to 30s.\n\n### Concrete changes\n\n#### 1. `tools/backend/backend.sh` — add timeout variable (after line 38)\nInsert after `STOP_GRACE`:\n```bash\nGEN_BACKEND_CONFIG_TIMEOUT=\"${GEN_BACKEND_CONFIG_TIMEOUT:-30}\" # seconds before config gen falls back to static\n```\n\n#### 2. `tools/backend/backend.sh` — add `_timeout_cmd` helper (after `load_env_file_if_unset`, before `# --- Subcommands`)\nAdd a function that:\n- Takes `<seconds> <cmd...>` as arguments\n- Backgrounds the command in its own process group via `set -m`\n- Runs a timer subshell that kills the process group on expiry and writes a flag file\n- Waits for the command, cleans up the watchdog (pkill -P + kill process group), and returns 124 if the flag file exists\n- Plays well with `set -euo pipefail` by returning non-zero rather than calling `exit`\n\n#### 3. `tools/backend/backend.sh` — wrap gen-backend-config call (line 204)\nChange:\n```bash\nif \"${REPO_ROOT}/visual-relay\" gen-backend-config \"${CONFIG}\" >\"${generated}\" 2>/tmp/.vr-gen-stderr; then\n```\nTo:\n```bash\nif _timeout_cmd \"${GEN_BACKEND_CONFIG_TIMEOUT}\" \"${REPO_ROOT}/visual-relay\" gen-backend-config \"${CONFIG}\" >\"${generated}\" 2>/tmp/.vr-gen-stderr; then\n```\nThis preserves stdout/stderr redirects — they apply to the function call as a whole. On timeout (exit 124) the `else` branch at line 207 fires with the static config.\n\n#### 4. `tests/VisualRelay.Tests/Installer5BackendShTests.cs` — static analysis tests\nAdd three new `[Fact]` methods:\n- **BackendSh_HasGenBackendConfigTimeout**: asserts `GEN_BACKEND_CONFIG_TIMEOUT` appears in backend.sh\n- **BackendSh_HasTimeoutCmd**: asserts `_timeout_cmd` function name appears\n- **BackendSh_UsesTimeoutCmdForGenBackendConfig**: asserts the gen-backend-config invocation line contains `_timeout_cmd`\n\n#### 5. `tests/VisualRelay.Tests/Installer5BackendShTests.Runtime.cs` — runtime timeout test\nAdd one new `[Fact]` method:\n- **Start_GenBackendConfigHang_FallsBackToStatic**: builds a fake repo with a stubbed `visual-relay` that sleeps forever when called as `gen-backend-config`. Runs `backend.sh start` with `GEN_BACKEND_CONFIG_TIMEOUT=2`. Asserts the script exits (doesn't hang), and stderr contains \"gen-backend-config unavailable; using static config\" (the fallback message). The existing test stubs must be updated — their `visual-relay` stubs already exit 0 quickly, so they pass under the timeout. Existing tests are unaffected.\n\n### Edge cases handled\n- **set -euo pipefail**: `_timeout_cmd` returns non-zero (124) rather than calling `exit`, so the `||` in `cmd_rc=0; wait ... || cmd_rc=$?` pattern and the caller's `if ...; then...else` both work correctly under errexit.\n- **macOS compatibility**: `pkill -P` is guarded by `|| true` (unsupported on macOS); the fallback `kill -- -$pid` handles cleanup.\n- **stdout/stderr redirects**: preserved because redirections apply to the function call; the timer subshell writes only to the flag file and stderr.\n- **Both execution paths covered**: the timeout wraps the entire `visual-relay gen-backend-config` invocation, which internally dispatches to either `exec PUBLISHED_GC` or `dotnet run`.\n- **Existing tests**: all current BackendShTests stubs exit 0 quickly, well under the 30s default, so they pass unchanged.",
  "manifest": [
    "tools/backend/backend.sh",
    "tests/VisualRelay.Tests/Installer5BackendShTests.cs",
    "tests/VisualRelay.Tests/Installer5BackendShTests.Runtime.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/Installer5BackendShTests.GenConfigTimeout.cs",
    "tests/VisualRelay.Tests/Installer5BackendShTests.cs"
  ],
  "rationale": "Created 6 tests (3 static analysis, 3 runtime) for the gen-backend-config timeout feature. 4 correctly FAIL before implementation: 3 static tests assert GEN_CONFIG_TIMEOUT, VISUAL_RELAY_GEN_CONFIG_TIMEOUT, and a timeout guard exist in backend.sh; 1 runtime test asserts the hang is eliminated (currently times out at 15s via TaskCanceledException). 2 runtime tests PASS as regression guards for existing non-zero-exit fallback and success paths. All existing 10 BackendSh tests continue to pass. Files: new partial 232 lines, edited main 192 lines — both under 300."
}

## Stage 6 - Implement

{
  "summary": "## Implementation complete: gen-backend-config timeout in backend.sh\n\n### Changes to `tools/backend/backend.sh` (324→369 lines)\n\n1. **Line 39** — Added `GEN_CONFIG_TIMEOUT=\"${VISUAL_RELAY_GEN_CONFIG_TIMEOUT:-15}\"` (overridable, default 15s).\n\n2. **Lines 151–188** — Added `_gen_config_with_timeout` helper function:\n   - Prefers GNU `timeout` command when available (exits 124 on expiry).\n   - Falls back to a pure-bash background watchdog that mirrors `_timeout_watchdog` from the `visual-relay` launcher: backgrounds the command in its own process group via `set -m`, runs a timer subshell that kills the process group on expiry and writes a flag file, then returns 124 (GNU timeout convention) so the fallback fires naturally.\n   - Plays well with `set -euo pipefail`: returns non-zero rather than calling `exit`, and the caller's `if …; then … else` catches the return code.\n   - stdout/stderr redirects are applied inside the helper, preserving existing behavior.\n\n3. **Lines 242–255** — Replaced the bare `gen-backend-config` invocation with `_gen_config_with_timeout`:\n   - On success (exit 0): uses generated config (unchanged).\n   - On timeout (exit 124): logs `\"gen-backend-config timed out after ${GEN_CONFIG_TIMEOUT}s; using static config\"` and falls through to static config.\n   - On other non-zero exit: logs `\"gen-backend-config unavailable (exit ${gen_rc}); using static config\"` (preserves existing graceful-degradation path).\n\n### Tests (all 813 pass, 0 fail, 0 skip)\n\n- **3 static tests** in `Installer5BackendShTests.cs` (lines 154–190): assert `GEN_CONFIG_TIMEOUT`, `VISUAL_RELAY_GEN_CONFIG_TIMEOUT`, and a timeout guard exist in backend.sh — all pass.\n- **3 runtime tests** in `Installer5BackendShTests.GenConfigTimeout.cs` (232 lines):\n  - `GenConfigTimeout_TimedOut_FallsBackToStaticConfig`: mock visual-relay sleeps 30s, `VISUAL_RELAY_GEN_CONFIG_TIMEOUT=2`, asserts script completes <15s, logs \"timed out\" and \"using static config\" — **passes**.\n  - `GenConfigTimeout_NonZeroExit_FallsBackToStaticConfig`: mock exits 1, asserts \"gen-backend-config unavailable\" log — **passes** (existing path preserved).\n  - `GenConfigTimeout_Success_UsesGeneratedConfig`: mock writes valid YAML, asserts generated file created with expected content — **passes**.\n- **10 existing BackendSh tests** in `Installer5BackendShTests.cs` and `Installer5BackendShTests.Runtime.cs` — all continue to pass."
}

## Stage 7 - Review

{
  "verdict": "changes",
  "issues": [
    "tools/backend/backend.sh: the `timeout` path uses plain GNU `timeout` without `--kill-after`. If `visual-relay gen-backend-config` catches or ignores SIGTERM, `timeout` waits indefinitely and `backend.sh start` can still hang past `GEN_CONFIG_TIMEOUT`. Add `timeout --kill-after=1s ...` and normalize exit code 137 (SIGKILL) to 124 so both the `timeout` and watchdog paths are hard bounds."
  ]
}

## Stage 8 - Fix

{
  "summary": "Fixed the review issue in `tools/backend/backend.sh` lines 157-167: added `--kill-after=1s` to the GNU `timeout` command so a process that ignores SIGTERM gets SIGKILL after 1 second (preventing `timeout` itself from hanging), and normalized exit code 137 (128+SIGKILL, returned by some coreutils versions when `--kill-after` fires) to 124 so the caller's `(( gen_rc == 124 ))` check correctly identifies the timeout case and logs \"timed out after Xs\". The watchdog fallback path already returned 124 and needed no change. All 813 tests pass (0 fail, 0 skip)."
}

## Stage 9 - Verify

{
  "summary": "Added a configurable timeout guard (`GEN_CONFIG_TIMEOUT`, default 15s, overridable via `VISUAL_RELAY_GEN_CONFIG_TIMEOUT`) to the `gen-backend-config` invocation in `backend.sh`. The `_gen_config_with_timeout` helper uses GNU `timeout --kill-after=1s` when available (normalizing exit 137→124) and falls back to a pure-bash background-kill watchdog. On timeout (exit 124) the script logs the distinct message \"gen-backend-config timed out after Xs; using static config\" and falls through to the static config path, eliminating the previously indefinite hang that blocked backend startup when `nix develop` or `dotnet` was wedged. The existing non-zero-exit graceful-degradation path is preserved unchanged. All 16 BackendSh tests (10 existing + 3 new static + 3 new runtime) pass. The hang is eliminated: `VISUAL_RELAY_GEN_CONFIG_TIMEOUT=1` with a mock `visual-relay gen-backend-config` that sleeps 30s completes in <5s and starts litellm on the static config.",
  "commitMessages": [
    "fix(backend): bound gen-backend-config with a timeout so a wedged generator cannot hang startup",
    "fix(backend): timeout gen-backend-config call in backend.sh; degrade to static config on expiry",
    "fix: add GEN_CONFIG_TIMEOUT guard to backend.sh start to prevent nix/dotnet hangs from blocking litellm boot",
    "fix(backend): wrap gen-backend-config in _gen_config_with_timeout with 15s default and watchdog fallback",
    "fix: eliminate unbounded hang in backend.sh gen-backend-config via timeout wrapper with graceful static-config fallback"
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "Verified the pinned suite (`dotnet test --filter \"FullyQualifiedName~Installer5BackendShTests\" --no-restore`): all 16 BackendSh tests pass (0 fail, 0 skip, exit 0). The gen-backend-config timeout guard (`_gen_config_with_timeout` with `--kill-after=1s` and exit-137→124 normalization), the 15s default overridable via `VISUAL_RELAY_GEN_CONFIG_TIMEOUT`, and the distinct timeout-vs-error logging are all working correctly. No failures to fix."
}

## Stage 11 - Commit

Committed by Visual Relay.

