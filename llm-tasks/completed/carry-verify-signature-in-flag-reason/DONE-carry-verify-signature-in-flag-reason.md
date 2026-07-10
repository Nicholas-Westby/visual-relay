# Carry the concrete failure signature in NEEDS-REVIEW flag reasons

When stage 10/11 exhausts its attempts, the task is flagged with the generic reason
"verify failed after 3 fix-verify attempts". In three real foreign-repo runs (express, axios, zod)
the actual cause was environmental (`sh: line 1: mocha: command not found`; `EPERM open …/.vite-temp/…`)
and identical across every attempt — but neither the UI task row, `selectedTask.reviewReason` in
`GET /state`, nor the NEEDS-REVIEW first line carried any of that. Diagnosis required opening
`.relay/<task>/run.log` and the verify-output artifacts on disk. An operator driving VR through the
control API (per the project's operating principle: every failure must be diagnosable from
`/state` + `/screenshot` alone) sees only the attempt count.

## What to build

1. **Enrich the flag reason for verify-exhaustion flags.** Where the driver flags a task after the
   fix-verify loop exhausts (`RelayDriver.VerifyFix.cs` / the FlagAsync call sites for
   "verify failed after N fix-verify attempts"), append the concrete signature of the LAST verify
   failure: the `reason` line already captured in the `verify_result` event (first line / first
   ~200 chars of the failure output, e.g. `sh: line 1: mocha: command not found`) plus the
   verify-output artifact path. Example final reason:
   `verify failed after 3 fix-verify attempts — last: sh: line 1: mocha: command not found (see .relay/<task>/stage11-attempt3.verify-output.txt)`.
2. **Same-signature advisory.** When ALL exhausted attempts share an identical failure signature
   (normalized: strip timestamps/paths that embed attempt numbers), append a marker like
   `— identical failure across all attempts; likely environment/harness, not the change` to the
   reason and emit a `warn` event. No behavior change to retries themselves in this task.
3. **Propagate everywhere the reason already flows**: NEEDS-REVIEW file first line, `/state`
   `tasks[].needsReview`+`selectedTask.reviewReason`, and the UI task row tooltip/label — these all
   read the same string today; confirm no truncation hides the appended detail (widen any hard
   caps to fit ~300 chars).

## Constraints

- General-purpose only; the signature is whatever the verify runner captured, no per-toolchain parsing.
- Do not alter retry/escalation counts or verify semantics — message/observability only.

## Tests (red first)

- Driver test: exhausted fix-verify loop with a stable failing signature produces a flag reason
  containing the signature text and the artifact path; the identical-signature advisory appears.
- Differing-signature case: reason carries the LAST signature, no identical-failure marker.
- Control-API state test: `reviewReason` in `/state` includes the enriched text.

## Verification

- `./test.sh` fully green including new tests.
