#!/usr/bin/env bash
#
# me.sh — claim authorship on the last N commits and strip Claude trailers.
#
# Thin wrapper. The claim+strip logic now lives in C# at
# tools/VisualRelay.ClaimAuthorship (VisualRelay.Core.Authorship); this script
# only locates that project and execs it against the CURRENT working directory's
# repo (dotnet run does not change cwd). All arg/env handling lives in the tool.
#
# Usage: me.sh [-N]
#   -N            commits back from HEAD to consider (default: 5)
#   CLAIM_EMAIL   force the claim email (must contain '@')
#   CLAIM_NAME    force the claim name (defaults to CLAIM_EMAIL's local-part)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet run --project \
  "$SCRIPT_DIR/tools/VisualRelay.ClaimAuthorship/VisualRelay.ClaimAuthorship.csproj" -- "$@"
