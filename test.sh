#!/usr/bin/env bash
# test.sh — thin wrapper over `./visual-relay test`.
#
# The real logic (host/VM-safe timestamped log dir, NO_BUILD/filter args,
# console+trx loggers, and TRX failed-test extraction) now lives in the tested
# C# TestRunner behind `visual-relay test`. This wrapper exists only so the
# familiar `./test.sh [FilterToken]` entry point keeps working.
#
#   ./test.sh [FilterToken]    # → ./visual-relay test [FilterToken]
#   NO_BUILD=1 ./test.sh [...] # NO_BUILD is read by the C# TestRunner
#   VR_TEST_LOG_DIR=... ./test.sh
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/visual-relay" test "$@"
