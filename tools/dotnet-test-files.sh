#!/bin/sh
# Stage-5 red-gate narrower for this repo's testFileCmd. dotnet test filters by
# test NAME, not file path, so map each authored .cs test file to a
# FullyQualifiedName~<Class> clause (file stem == test class by convention) and
# run only those. With no .cs args the filter stays empty and dotnet runs the
# full suite — the same shape a {files}-less testFileCmd would have.
set -eu
proj="tests/VisualRelay.Tests/VisualRelay.Tests.csproj"
filter=""
for f in "$@"; do
  case "$f" in *.cs) ;; *) continue ;; esac
  n=${f##*/}; n=${n%.cs}; n=${n%%.*}
  case "$filter" in "") filter="FullyQualifiedName~$n" ;; *) filter="$filter|FullyQualifiedName~$n" ;; esac
done
if [ -n "$filter" ]; then
  exec dotnet test "$proj" -m:1 -p:UseSharedCompilation=false --blame-hang --blame-hang-timeout 20s --blame-hang-dump-type none --filter "$filter"
fi
exec dotnet test "$proj" -m:1 -p:UseSharedCompilation=false --blame-hang --blame-hang-timeout 20s --blame-hang-dump-type none
