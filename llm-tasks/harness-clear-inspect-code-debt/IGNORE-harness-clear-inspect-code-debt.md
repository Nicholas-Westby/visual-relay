# Harness: clear the pre-existing inspect-code zero-findings debt

> **IGNORE (deferred, not abandoned).** Pre-existing debt — `HEAD` already failed the
> inspect-code gate before the 2026-06-28 task run. Un-IGNORE to address.

## Context
`./visual-relay check` includes an `inspect-code` zero-findings gate that **HEAD already
fails** (observed ~54 findings, in files unchanged by recent work — i.e. the baseline tree
is red on this gate). NOTE: the per-task **verify** gate (`guardCmd` = source-enumeration +
file-size + format, then `testCmd`) does NOT run inspect-code, so this does not block LLM
tasks — it only makes the developer `check` red. The 3 `MergeIntoPattern` findings in the
new `tools/VisualRelay.Guards/RealBuildSubprocessGuard.cs` are part of the same set.

## What to do
Clear the inspect-code findings (or justifiably suppress) so `./visual-relay check` reaches
green on the inspect-code step. This touches ~25 files; do it as focused commits and keep
each change behavior-preserving. Re-confirm the full suite stays green under nono.

## Why deferred
Large, low-risk-but-broad cleanup, unrelated to the verify-reliability blocker or the 10 UI/
harness tasks; touches files owned by other tasks, so best done as its own pass.
