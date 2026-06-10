# A single malformed stage completion (missing fenced JSON) flags the whole task

When a stage's subagent finishes successfully but its final output lacks the required
fenced ```json contract block, the runner gives up immediately:
`SwivalSubagentRunner` returns `SubagentResult` with
`ErrorHintClassifier.WithHint("no valid fenced json block")`
(`src/VisualRelay.Core/Execution/ProcessRunners.cs:139`) and the driver flags the task.
There is no corrective retry and no tier escalation — in contrast to the stall/timeout
path, which gets `maxStallRetries` attempts.

Observed (2026-06-09, sandbox-3 drive): the cheap-tier Ideate model produced a complete,
high-quality ideation document (framing, three options, trade-offs — preserved verbatim
in the task's NEEDS-REVIEW file) but ended without the fenced JSON verdict block. The
entire task run died at stage 1 after ~2 min. The work was done; only the envelope was
missing. Cheap-tier models misformat regularly, so on long queues this failure mode is a
constant tax: each occurrence wastes the whole run and requires a human/agent re-drive.

## Goal

A conformant answer that arrives without its JSON envelope is recovered automatically:
the runner makes a bounded corrective attempt (and optionally a tier escalation) before
flagging. Contract failures become rare, visible (evented), and cheap — never an instant
task death after otherwise-successful work.

## Approach (suggested)

- On `json is null` after a zero-exit run, re-invoke the subagent on the **same tier**
  with the prior output attached and a corrective instruction: the previous completion
  was missing the fenced ```json block required by the stage contract; reply with ONLY
  that block (derive it from the prior answer — do not redo the work). One cheap turn
  usually suffices since the reasoning already happened.
- Bound it: a `maxContractRetries` config (default 1–2), mirroring `maxStallRetries`
  loading in `RelayConfigLoader`. Consider one final attempt on the next-higher tier
  when same-tier correction fails, then flag with the existing hint.
- Emit a `contract_retry` event (like stall retries) so drives/logs show the recovery.
- Tests (the suite already fakes the runner — see `TestDoubles.cs` /
  `SwivalSubagentRunnerTests.cs`): (a) first output lacks the block, corrective call
  returns it → stage succeeds, one `contract_retry` evented; (b) corrective prompt
  contains the prior output and only-the-block instruction; (c) exhaustion still flags
  with `no valid fenced json block`; (d) `maxContractRetries: 0` preserves today's
  fail-fast behavior.
