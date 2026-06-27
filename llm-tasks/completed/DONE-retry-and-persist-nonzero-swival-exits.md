# Nonzero swival exits flag instantly with no evidence — persist output and retry bounded

Two stage-8 flags on 2026-06-11 share the signature `swival exit 1: nono v0.62.0
<startup banner>` — because the flag reason is `TrimForError` (the FIRST 600 chars of
captured output, i.e. the sandbox banner), while the actual error sits at the TAIL,
and the nonzero-exit path neither persists the captured output (the stall path does,
since 227b937) nor retries. The two cases prove both halves matter:

- fix-timing-estimates s8: ran 3.5 min, model emitted a `todo` tool call with
  malformed JSON args, swival injected its corrective message, exited 1 five seconds
  later. Model-stochastic + swival's recovery path dying — a retry would very likely
  have succeeded. The real traceback was lost with the unpersisted output tail.
- drop-vestigial-kimi-suffix s8: died in 2s on a litellm 400 (invalid model after the
  task's own profile edit — separately spec'd as profile pinning). A retry would have
  refailed identically and flagging is correct; the evidence loss is not.

## Goal

A swival process that exits nonzero without producing a valid report is treated like
a stall, not a verdict: its full captured stdout/stderr is persisted to
`stageN-attemptM.killed-output.txt` (same artifact the stall path writes), and the
attempt is retried within the same `MaxStallRetries` budget the stall path uses
(shared budget — combined stall+crash attempts stay bounded). When retries exhaust,
the flag reason includes the TAIL of the output (where errors actually are), not just
the head, plus the persisted path.

## Approach (suggested)

- In the runner's nonzero-exit branch: persist via the existing helper, then route
  into the existing stall-retry decrement/continue instead of returning immediately;
  on exhaustion, build the reason from the last N hundred chars (tail) + path.
- Keep genuine fail-fast cases fail-fast where they already are (backend not ready,
  empty whitelist) — this change is only for the spawned process dying nonzero.
- Tests mirror the watchdog tests with a fake swival that exits 1 with stderr on the
  first attempt and succeeds on the second (asserts retry + persisted file + success),
  plus an always-exit-1 fake (asserts bounded attempts, tail-not-head in the reason).
