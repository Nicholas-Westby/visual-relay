#!/usr/bin/env bash
# Thin wrapper — the verify logic now lives in C# at VisualRelay.Guards/VerifyRunner.cs.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet run --project "$SCRIPT_DIR/VisualRelay.Guards/VisualRelay.Guards.csproj" -- verify
