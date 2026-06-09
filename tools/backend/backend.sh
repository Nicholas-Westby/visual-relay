#!/usr/bin/env bash
# Lifecycle manager for Visual Relay's local model backend (a LiteLLM proxy).
#
# Subcommands: start | stop | status
#
# The proxy is served on http://127.0.0.1:4000 and answers readiness at
# /health/readiness, matching src/VisualRelay.Domain/ModelBackend.cs. start is
# idempotent (a healthy instance => exit 0, no duplicate process), stale-pid
# safe, polls readiness before returning, and degrades gracefully with a clear
# message + non-zero exit when the litellm toolchain is missing. stop SIGTERMs
# then SIGKILLs and ALWAYS removes the pidfile so the next start is unblocked.
set -euo pipefail

# --- Paths (resolve repo root from this script's location) -------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

HOST="127.0.0.1"
PORT="4000"
BASE_URL="http://${HOST}:${PORT}"          # == ModelBackend.BaseUrl
READINESS_URL="${BASE_URL}/health/readiness" # == ModelBackend.ReadinessUrl
CONFIG="${SCRIPT_DIR}/litellm-config.yaml"  # static template; may be replaced by generated config

PYTHON_VERSION="3.13"                          # litellm's uvloop crashes on 3.14+
VENV_DIR="${SCRIPT_DIR}/.venv"                 # git-ignored; provisioned via uv
VENV_PY="${VENV_DIR}/bin/python"
LITELLM_BIN="${VENV_DIR}/bin/litellm"

SCRATCH="${REPO_ROOT}/.relay-scratch"        # git-ignored
PID_FILE="${SCRATCH}/litellm.pid"
LOG_FILE="${SCRATCH}/litellm.log"

READY_TIMEOUT="${VISUAL_RELAY_BACKEND_TIMEOUT:-30}" # seconds to wait for readiness
STOP_GRACE="${VISUAL_RELAY_BACKEND_STOP_GRACE:-10}" # seconds before SIGKILL

log() { echo "backend: $*" >&2; }

# --- Helpers -----------------------------------------------------------------

# 200 from the readiness endpoint => a healthy proxy is already up.
is_healthy() {
  curl -fsS -o /dev/null --max-time 2 "${READINESS_URL}" 2>/dev/null
}

# Read a live pid from the pidfile, or empty string. A pidfile whose process is
# gone (kill -0 fails) is treated as stale and reported as empty.
live_pid() {
  [[ -f "${PID_FILE}" ]] || return 0
  local pid
  pid="$(cat "${PID_FILE}" 2>/dev/null || true)"
  [[ -n "${pid}" ]] || return 0
  if kill -0 "${pid}" 2>/dev/null; then
    echo "${pid}"
  fi
}

# Make a litellm executable available and set LITELLM_BIN. Prefers a project-local
# venv pinned to a litellm-compatible Python (uvloop crashes on 3.14+), which uv
# provisions on first run (uv fetches the pinned Python itself). Falls back to a
# litellm already on PATH only when uv is unavailable. Returns non-zero when none
# of those work.
ensure_litellm() {
  if [[ -x "${LITELLM_BIN}" ]]; then
    return 0
  fi

  if command -v uv >/dev/null 2>&1; then
    log "provisioning litellm into ${VENV_DIR} (one-time; Python ${PYTHON_VERSION})"
    if uv venv "${VENV_DIR}" --python "${PYTHON_VERSION}" >&2 \
       && uv pip install --python "${VENV_PY}" "litellm[proxy]" >&2 \
       && [[ -x "${LITELLM_BIN}" ]]; then
      return 0
    fi
    log "uv could not provision litellm (see output above)"
    return 1
  fi

  # Fallback: a litellm already on PATH (may run on a Python that crashes uvloop).
  if command -v litellm >/dev/null 2>&1; then
    LITELLM_BIN="$(command -v litellm)"
    log "using PATH litellm at ${LITELLM_BIN} (install uv for a pinned Python ${PYTHON_VERSION} venv)"
    return 0
  fi

  return 1
}

missing_toolchain_message() {
  cat >&2 <<EOF
backend: could not start the model backend (litellm) on ${BASE_URL}.

  Launch normally provisions litellm into ${VENV_DIR} using uv. To enable it:
    1. Install uv (one-time):  curl -LsSf https://astral.sh/uv/install.sh | sh
       (uv fetches a pinned Python ${PYTHON_VERSION} and litellm[proxy] for you.)
    2. Provide provider keys:  see .env.example and place them in
       ~/.config/visual-relay/.env (user-level, always writable).
    3. Start it again:         tools/backend/backend.sh start

Visual Relay still launches without the backend; tasks that call the model will
surface a "backend down" message from the in-app pre-flight probe.
EOF
}

# Source a dotenv file, exporting each KEY=VALUE only when that variable is not
# already set in the environment (process env always wins). Blank lines and
# #-comments are skipped; surrounding single/double quotes are stripped from
# values so both KEY=val and KEY="val" round-trip correctly.
load_env_file_if_unset() {
  local file="$1"
  [[ -f "${file}" ]] || return 0
  while IFS= read -r line || [[ -n "${line}" ]]; do
    # Trim leading/trailing whitespace.
    local trimmed
    trimmed="${line#"${line%%[![:space:]]*}"}"
    trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
    # Skip blanks (including whitespace-only) and comments.
    [[ -z "${trimmed}" || "${trimmed}" == "#"* ]] && continue
    # Split on first '='; skip lines without '='.
    [[ "${trimmed}" == *"="* ]] || continue
    local key="${trimmed%%=*}"
    local value="${trimmed#*=}"
    # Trim whitespace from the key; skip empty keys (e.g. " =value").
    key="${key#"${key%%[![:space:]]*}"}"
    key="${key%"${key##*[![:space:]]}"}"
    [[ -n "${key}" ]] || continue
    # Strip surrounding quotes (single or double) from the value.
    if [[ "${value}" =~ ^\"(.*)\"$ || "${value}" =~ ^\'(.*)\'$ ]]; then
      value="${BASH_REMATCH[1]}"
    fi
    # Only export when the variable is not already set in the process env.
    if [[ -z "${!key:-}" ]]; then
      export "${key}=${value}"
    fi
  done < "${file}"
}

# --- Subcommands -------------------------------------------------------------

cmd_start() {
  mkdir -p "${SCRATCH}"

  if is_healthy; then
    log "already healthy at ${BASE_URL} (no-op)"
    return 0
  fi

  # A live pid without health means it is still booting or wedged; a missing or
  # stale pidfile means we are free to (re)start. Either way, clear a stale file.
  local existing
  existing="$(live_pid)"
  if [[ -n "${existing}" ]]; then
    log "process ${existing} is running but not yet healthy; waiting for readiness"
  else
    if [[ -f "${PID_FILE}" ]]; then
      log "removing stale pidfile ${PID_FILE}"
      rm -f "${PID_FILE}"
    fi

    if ! ensure_litellm; then
      missing_toolchain_message
      return 1
    fi

    # Load provider keys with 3-tier precedence:
    #   1. process env (already set)         ← always wins
    #   2. user-level ~/.config/visual-relay/.env
    #   3. repo .env (dev-only fallback)
    local user_env="${XDG_CONFIG_HOME:-$HOME/.config}/visual-relay/.env"
    if [[ -f "${user_env}" ]]; then
      log "loading provider keys from ${user_env}"
      load_env_file_if_unset "${user_env}"
    fi
    if [[ -f "${REPO_ROOT}/.env" ]]; then
      log "loading provider keys from ${REPO_ROOT}/.env (dev fallback)"
      load_env_file_if_unset "${REPO_ROOT}/.env"
    fi

    # Generate key-aware config from present provider keys; static fallback if unavailable.
    local generated="${SCRATCH}/litellm-config.generated.yaml"
    if "${REPO_ROOT}/visual-relay" gen-backend-config "${CONFIG}" >"${generated}" 2>/tmp/.vr-gen-stderr; then
      CONFIG="${generated}"
      [[ -s /tmp/.vr-gen-stderr ]] && log "$(head -n1 /tmp/.vr-gen-stderr)"
    else
      log "gen-backend-config unavailable; using static config"
    fi
    rm -f /tmp/.vr-gen-stderr

    # aiohttp doesn't enforce read timeout on the first response byte (a pre-stream
    # hang is unbounded). httpx applies stream_timeout from byte 0, so hangs fail fast.
    export DISABLE_AIOHTTP_TRANSPORT="${DISABLE_AIOHTTP_TRANSPORT:-True}"
    # Wall-clock cap per stream; on exceed litellm raises fallback-eligible timeout.
    export LITELLM_MAX_STREAMING_DURATION_SECONDS="${LITELLM_MAX_STREAMING_DURATION_SECONDS:-240}"

    log "starting litellm proxy on ${BASE_URL} (logs: ${LOG_FILE})"
    # Disown so the proxy outlives this script; capture stdout+stderr to the log.
    nohup "${LITELLM_BIN}" --config "${CONFIG}" --host "${HOST}" --port "${PORT}" \
      >"${LOG_FILE}" 2>&1 &
    echo "$!" >"${PID_FILE}"
    log "pid $(cat "${PID_FILE}") recorded at ${PID_FILE}"
  fi

  # Poll readiness up to the bounded timeout before handing off.
  local waited=0
  while (( waited < READY_TIMEOUT )); do
    if is_healthy; then
      log "ready at ${BASE_URL} after ${waited}s"
      return 0
    fi
    # If the process died while booting, fail fast with the log tail.
    if [[ -z "$(live_pid)" ]]; then
      log "litellm exited before becoming ready; last log lines:"
      tail -n 20 "${LOG_FILE}" >&2 2>/dev/null || true
      rm -f "${PID_FILE}"
      return 1
    fi
    sleep 1
    waited=$(( waited + 1 ))
  done

  log "timed out after ${READY_TIMEOUT}s waiting for ${READINESS_URL}; see ${LOG_FILE}"
  return 1
}

cmd_stop() {
  local pid
  pid="$(live_pid)"

  if [[ -z "${pid}" ]]; then
    if [[ -f "${PID_FILE}" ]]; then
      log "no live process; removing stale pidfile ${PID_FILE}"
      rm -f "${PID_FILE}"
    else
      log "not running (no pidfile)"
    fi
    return 0
  fi

  log "stopping pid ${pid} (SIGTERM)"
  kill -TERM "${pid}" 2>/dev/null || true

  local waited=0
  while (( waited < STOP_GRACE )); do
    if ! kill -0 "${pid}" 2>/dev/null; then
      break
    fi
    sleep 1
    waited=$(( waited + 1 ))
  done

  if kill -0 "${pid}" 2>/dev/null; then
    log "pid ${pid} still alive after ${STOP_GRACE}s; SIGKILL"
    kill -KILL "${pid}" 2>/dev/null || true
  fi

  # ALWAYS clean up the pidfile, even after an abrupt prior kill / already-dead.
  rm -f "${PID_FILE}"
  log "stopped; pidfile removed"
  return 0
}

cmd_status() {
  if is_healthy; then
    local pid
    pid="$(live_pid)"
    if [[ -n "${pid}" ]]; then
      echo "up: healthy at ${BASE_URL} (pid ${pid})"
    else
      echo "up: healthy at ${BASE_URL} (not managed by this script)"
    fi
    return 0
  fi

  local pid
  pid="$(live_pid)"
  if [[ -n "${pid}" ]]; then
    echo "down: process ${pid} running but ${READINESS_URL} not answering"
    return 1
  fi

  if [[ -f "${PID_FILE}" ]]; then
    echo "down: stale pidfile ${PID_FILE} (process gone)"
    return 1
  fi

  echo "down: no process at ${BASE_URL}"
  return 1
}

# --- Dispatch ----------------------------------------------------------------
case "${1:-}" in
  start)  cmd_start ;;
  stop)   cmd_stop ;;
  status) cmd_status ;;
  *)
    echo "usage: $(basename "$0") {start|stop|status}" >&2
    exit 2
    ;;
esac
