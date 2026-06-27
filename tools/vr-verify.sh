#!/usr/bin/env bash
# Full-suite verify for this repo. Reports pass/fail from the TRX result counters,
# so a post-pass testhost teardown SIGKILL (exit 137, from lingering MSBuild
# workers on a small host) does not fail a green run. Disables node reuse so
# those workers do not linger. Parse failure or a missing TRX fails conservatively.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
r=".vr-verify-results"; rm -rf "$r"; mkdir -p "$r"
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false --logger "trx;LogFileName=v.trx" --results-directory "$r"
code=$?
trx=$(ls "$r"/*.trx 2>/dev/null | head -1)
[ -z "$trx" ] && { echo "vr-verify: no TRX produced (build failure?); propagating exit $code"; exit "$code"; }
c=$(grep -oE '<Counters [^>]*/>' "$trx" | head -1)
g() { echo "$c" | grep -oE "$1=\"[0-9]+\"" | grep -oE '[0-9]+'; }
ran=$(g executed); fa=$(g failed); er=$(g error); ab=$(g aborted); to=$(g timeout)
echo "vr-verify: executed=${ran:-?} failed=${fa:-?} error=${er:-?} aborted=${ab:-?} timeout=${to:-?} (raw exit $code)"
if [ "${ran:-0}" -gt 0 ] && [ "${fa:-1}" = 0 ] && [ "${er:-0}" = 0 ] && [ "${ab:-0}" = 0 ] && [ "${to:-0}" = 0 ]; then
  [ "$code" -ne 0 ] && echo "vr-verify: all ${ran} tests passed; ignoring teardown exit $code"
  exit 0
fi
exit 1
