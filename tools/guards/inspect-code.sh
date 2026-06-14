#!/usr/bin/env bash
# InspectCode zero-findings gate — runs JetBrains InspectCode over the whole
# solution and exits non-zero if any finding appears at or above the floor.
#
# Floor: SUGGESTION (note-level).  Findings suppressed via .editorconfig
# (severity = none) do not appear in the SARIF at all, so the gate need only
# check whether any result is present.  InspectCode always exits 0 regardless
# of findings — the SARIF is the sole source of truth.
#
# Caches, downloads, and the SARIF output live under
#   ${XDG_CACHE_HOME:-$HOME/.cache}/visual-relay/inspectcode/
# — never the repo tree or a global location.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CACHE_ROOT="${XDG_CACHE_HOME:-$HOME/.cache}/visual-relay/inspectcode"
CACHES_HOME="$CACHE_ROOT/caches"
SARIF_PATH="$CACHE_ROOT/inspectcode.sarif.json"
TOOL_MANIFEST="$REPO_ROOT/.config/dotnet-tools.json"

# ---------- tool restore (idempotent) ----------
echo "inspect-code: restoring dotnet tools from $TOOL_MANIFEST" >&2
dotnet tool restore --tool-manifest "$TOOL_MANIFEST" >&2

# ---------- run InspectCode ----------
echo "inspect-code: running JetBrains InspectCode (floor: SUGGESTION)" >&2
mkdir -p "$CACHE_ROOT" "$CACHES_HOME"

dotnet jb inspectcode "$REPO_ROOT/VisualRelay.slnx" \
  --no-build \
  --output="$SARIF_PATH" \
  --caches-home="$CACHES_HOME" \
  --severity=SUGGESTION \
  --format=Sarif \
  >&2

# ---------- gate on zero findings ----------
# Parse the SARIF: count results across all runs.  The .editorconfig
# carve-out already removes suppressed inspections, so any remaining
# result is a real finding that must be addressed.
finding_count="$(python3 -c "
import json, sys
with open(sys.argv[1]) as f:
    sarif = json.load(f)
results = []
for run in sarif.get('runs', []):
    for r in run.get('results', []):
        results.append(r)
print(len(results))
" "$SARIF_PATH")"

if [[ "$finding_count" -eq 0 ]]; then
    echo "inspect-code: 0 findings — gate passed." >&2
    exit 0
fi

echo "inspect-code: $finding_count finding(s) at or above SUGGESTION floor." >&2
echo "SARIF: $SARIF_PATH" >&2
echo "Review each finding.  Fix real defects in code; only suppress via .editorconfig" >&2
echo "with a documented rationale.  Never carve out a real defect." >&2
exit 1
