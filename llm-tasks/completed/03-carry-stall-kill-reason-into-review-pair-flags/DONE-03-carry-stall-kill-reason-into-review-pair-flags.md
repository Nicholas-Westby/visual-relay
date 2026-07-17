## Task: Carry the stall-kill signature into review-pair flag reasons and retry infra kills

When a review-pair stage's agent process is killed by the watchdog, the run is
flagged with the generic reason `Review returned an invalid result`. The kill
context — reason, last-signal source, autopsy artifact path — exists in the run
log and in the killed-output file, but the flag reason, the NEEDS-REVIEW file,
and the queue card show only the generic string. The operator cannot tell an
infrastructure kill from the model actually emitting garbage, and the stage is
never retried even though an infrastructure kill is exactly the retryable case.

This mirrors the already-completed task
`llm-tasks/completed/carry-verify-signature-in-flag-reason/` which fixed the
same reporting gap for verify-exhaustion flags. The review pair needs the same
treatment.

### Evidence (task `hoist-pipeline-test-shared-setup`, 2026-07-15)

- Review (stage 7, frontier tier) ran ~55 minutes and was watchdog-killed:
  run-log event `s7/frontier stall_kill reason: absolute_ceiling lastSignal: cpu
  silenceMs: 52373 …
  outputSaved: .relay/hoist-pipeline-test-shared-setup/stage7-attempt1.killed-output.txt`.
  The autopsy file shows the swival agent was blocked in a synchronous
  `sock.recv()` waiting for LLM response headers (a hung HTTP call to the
  litellm proxy), then SIGINT'd (`KeyboardInterrupt`, exit 130).
- `RunStageAsync` (`src/VisualRelay.Core/Execution/RelayDriver.ReviewPair.cs:186-205`)
  maps any invalid/empty result to `Check="red"` with no distinction for
  kills; `RunReviewPairAsync` then flags with the fixed string
  `"Review returned an invalid result"` (lines 82, 115; visual sibling at 88,
  136). The stall-kill reason is dropped.
- Only `stage7-attempt1.*` exists — no second attempt. Other stages escalate
  and retry (see completed task `make-stage-retries-always-escalate`); the
  review pair path calls the subagent exactly once.
- The task burned stages 1-6 (~21 minutes, ~$0.27) and produced a valid
  implementation, all discarded to NEEDS-REVIEW because one LLM HTTP request
  hung.

### What to build

1. Thread the kill information from the subagent runner into `StageRunResult`
   (e.g. a nullable `KillSignature` with reason, lastSignal, silence, autopsy
   path). The runner already knows it — it wrote the `stall_kill` event and the
   killed-output artifact (`preserve-trace-when-stage-killed` machinery).
2. When a review-pair stage result is red BECAUSE the process was killed, flag
   with an enriched reason, e.g.
   `Review stall-killed (absolute_ceiling, lastSignal=cpu) after 55m — see .relay/<task>/stage7-attempt1.killed-output.txt`
   instead of `Review returned an invalid result`. Keep the generic wording for
   genuinely invalid model output.
3. Retry policy: on a watchdog kill (not on invalid model output), give the
   review-pair stage one escalated retry (attempt2) before flagging, consistent
   with `make-stage-retries-always-escalate` semantics elsewhere. Preserve the
   sibling-survives-failure semantics of the pair (`RelayDriver.ReviewPair.cs:104-116`).
4. Propagate the enriched reason everywhere the reason already flows:
   NEEDS-REVIEW first line, `/state` `reviewReason`, queue card label, drain
   log — same checklist as the completed verify-signature task.

### Constraints

- Message/observability + one bounded retry only; no changes to review
  contracts or triage routing.
- Repo-agnostic wording (the signature comes from the watchdog, not from
  parsing agent output).
- Keep `RelayDriver.ReviewPair.cs` under the 300-line guard (helpers live in
  sibling partials).

### Tests (red first)

- Driver test with a fake subagent runner that reports a watchdog kill:
  flag reason contains the kill reason and autopsy path; a retry attempt was
  made first; attempt2 success proceeds normally without a flag.
- Invalid-output (no kill) case: single attempt, existing generic reason —
  behavior unchanged.
- Control-API/state test: enriched reason visible in `reviewReason`.

### Verification

- `./visual-relay check` fully green including the new tests.
