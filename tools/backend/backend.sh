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
CONFIG="${SCRIPT_DIR}/litellm-config.yaml"

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

# Discover how to launch litellm: the standalone CLI, else `python3 -m litellm`.
# Echoes the launcher words; empty output => toolchain unavailable.
litellm_launcher() {
  if command -v litellm >/dev/null 2>&1; then
    echo "litellm"
  elif command -v python3 >/dev/null 2>&1 && python3 -c "import litellm" >/dev/null 2>&1; then
    echo "python3 -m litellm"
  fi
}

missing_toolchain_message() {
  cat >&2 <<EOF
backend: litellm is not installed, so the model backend cannot be started.

  The proxy provides ${BASE_URL} for Visual Relay's profiles. To enable it:
    1. Install the proxy:   pip install 'litellm[proxy]'
    2. Provide provider keys: cp .env.example .env  (then fill in the keys)
    3. Start it again:        tools/backend/backend.sh start

Visual Relay can still launch without the backend; tasks that call the model
will surface a "backend down" message from the in-app pre-flight probe.
EOF
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

    local launcher
    launcher="$(litellm_launcher)"
    if [[ -z "${launcher}" ]]; then
      missing_toolchain_message
      return 1
    fi

    # Split into words on purpose: the launcher may be "python3 -m litellm".
    local -a launcher_cmd
    read -ra launcher_cmd <<<"${launcher}"

    # Load provider keys from .env if present (keys are read via os.environ).
    if [[ -f "${REPO_ROOT}/.env" ]]; then
      log "loading provider keys from .env"
      set -a
      # shellcheck disable=SC1091
      . "${REPO_ROOT}/.env"
      set +a
    fi

    log "starting litellm proxy on ${BASE_URL} (logs: ${LOG_FILE})"
    # Disown so the proxy outlives this script; capture stdout+stderr to the log.
    nohup "${launcher_cmd[@]}" --config "${CONFIG}" --host "${HOST}" --port "${PORT}" \
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
