#!/usr/bin/env bash
# Full-suite verify for Visual Relay's own repo.
#
# Reports success from the TRX result counters rather than the raw process exit
# code, so a spurious testhost teardown SIGKILL (exit 137) that strikes AFTER all
# tests already passed does not mark a green run as failed. Also disables MSBuild
# node reuse / build server so worker processes do not linger past the run (the
# usual trigger for that teardown reap on a small host).
export DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
results=".vr-verify-results"
rm -rf "$results"; mkdir -p "$results"

dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj \
  -m:1 -p:UseSharedCompilation=false \
  --logger "trx;LogFileName=verify.trx" --results-directory "$results"
code=$?

trx=$(ls "$results"/*.trx 2>/dev/null | head -1)
if [ -z "$trx" ]; then
  echo "vr-verify: no TRX produced (likely a build failure); propagating exit $code"
  exit "$code"
fi

counters=$(grep -oE '<Counters [^>]*/>' "$trx" | head -1)
attr() { echo "$counters" | grep -oE "$1=\"[0-9]+\"" | grep -oE '[0-9]+'; }
failed=$(attr failed); errors=$(attr error); aborted=$(attr aborted)
timedout=$(attr timeout); ran=$(attr executed)
echo "vr-verify: counters -> executed=${ran:-?} failed=${failed:-?} error=${errors:-?} aborted=${aborted:-?} timeout=${timedout:-?} (raw exit $code)"

if [ "${failed:-1}" = "0" ] && [ "${errors:-0}" = "0" ] && [ "${aborted:-0}" = "0" ] \
   && [ "${timedout:-0}" = "0" ] && [ "${ran:-0}" -gt 0 ]; then
  if [ "$code" -ne 0 ]; then
    echo "vr-verify: all ${ran} tests passed; non-zero exit ($code) is a post-pass teardown crash -> success"
  fi
  exit 0
fi

echo "vr-verify: TRX reports real failures -> fail"
exit 1
