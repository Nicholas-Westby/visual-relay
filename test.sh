#!/usr/bin/env bash
# test.sh — Run VisualRelay.Tests and persist ALL output so failures are diagnosable.
#
# Usage:
#   ./test.sh [FilterToken]       # FilterToken becomes --filter "FullyQualifiedName~<token>"
#   NO_BUILD=1 ./test.sh [...]    # Skip build (default: builds first for correctness)
#
# Logs land in ./test-logs/ (gitignored) or VR_TEST_LOG_DIR if set.
# Filenames include timestamp + short hostname + PID so concurrent runs
# from host AND VM never collide (the working folder is shared between machines).
#
# On failure: parses the TRX for failing test names and prints them as a bullet list.

set -uo pipefail

PROJ="tests/VisualRelay.Tests/VisualRelay.Tests.csproj"
LOG_DIR="${VR_TEST_LOG_DIR:-./test-logs}"
TIMESTAMP=$(date +%Y%m%dT%H%M%S)
HOST=$(hostname -s 2>/dev/null || echo "unknown")
STEM="${TIMESTAMP}_${HOST}_$$"

LOG_FILE="${LOG_DIR}/${STEM}.log"
TRX_FILE="${LOG_DIR}/${STEM}.trx"

mkdir -p "$LOG_DIR"

# Build optional args
EXTRA_ARGS=()
if [[ "${NO_BUILD:-}" == "1" ]]; then
    EXTRA_ARGS+=(--no-build)
fi
if [[ "${1:-}" != "" ]]; then
    EXTRA_ARGS+=(--filter "FullyQualifiedName~${1}")
fi

# Run dotnet test — pipe console output through tee so it shows live AND lands in the log.
# The trx logger writes into --results-directory with its own filename; we'll locate it below.
# NOTE: ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"} (not "${EXTRA_ARGS[@]}") — on macOS's
# bash 3.2, expanding an EMPTY array under `set -u` raises "unbound variable".
# This bites the bare `./test.sh` full-suite path (no NO_BUILD, no filter).
dotnet test "$PROJ" \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=${STEM}.trx" \
    --results-directory "$LOG_DIR" \
    ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"} 2>&1 | tee "$LOG_FILE"

TEST_EXIT="${PIPESTATUS[0]}"

# Report paths
echo >&2 ""
echo >&2 "Log file : $LOG_FILE"
echo >&2 "TRX file : $TRX_FILE"

if [[ "$TEST_EXIT" -ne 0 ]]; then
    echo >&2 ""
    echo >&2 "FAILING TESTS:"
    if [[ -f "$TRX_FILE" ]]; then
        grep '<UnitTestResult ' "$TRX_FILE" \
            | grep 'outcome="Failed"' \
            | sed -n 's/.*testName="\([^"]*\)".*/\1/p' \
            | sort -u \
            | while IFS= read -r name; do
                echo >&2 "  - $name"
              done
    else
        echo >&2 "  (TRX not found at $TRX_FILE — check log for details)"
    fi
fi

exit "$TEST_EXIT"
