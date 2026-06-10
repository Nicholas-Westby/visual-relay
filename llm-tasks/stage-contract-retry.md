# A single malformed stage completion (missing fenced JSON) flags the whole task

> **Amended 2026-06-09 after this task itself flagged at Diagnose:** the model's output
> DID contain a complete fenced ```json block (preserved in
> `.relay/stage-contract-retry/NEEDS-REVIEW`), but its JSON *string values quoted the
> phrase* "fenced ```json block" — and `FencedJsonExtractor` terminated the fence at the
> first interior ```, producing truncated JSON and returning null. Any stage whose
> subject matter mentions fenced blocks (every pipeline self-fix task) collides with a
> naive extractor. So this task has TWO root causes; fix both:
> **(1) Extractor robustness** — on parse failure, widen the candidate: try every
> ```json opening against every subsequent closing fence (or last-complete-block-first)
> and accept the first candidate that parses as valid JSON. Tests: ``` sequences inside
> JSON string values; multiple fenced blocks where only one parses; block at EOF without
> trailing newline; today's simple cases unchanged.
> **(2) Corrective retry** (below) for outputs where no parseable block exists at all.

When a stage's subagent finishes successfully but its final output lacks the required
fenced ```json contract block, the runner gives up immediately:
`SwivalSubagentRunner` returns `SubagentResult` with
`ErrorHintClassifier.WithHint("no valid fenced json block")`
(`src/VisualRelay.Core/Execution/ProcessRunners.cs:139`) and the driver flags the task
(`RelayDriver.cs:95-97`). There is no corrective retry and no tier escalation — in
contrast to the stall/timeout path, which gets `maxStallRetries` attempts via the loop
at `ProcessRunners.cs:68-141` (the contract-failure path returns straight out of it).

Observed twice on 2026-06-09:
- sandbox-3 drive: cheap-tier Ideate produced a complete, high-quality ideation document
  but ended without the fenced JSON verdict block; the task died at stage 1 after ~2 min.
- this task's own first drive: Diagnose emitted a well-formed block that the extractor
  mis-parsed (see amendment above); the task died at stage 3 after ~4 min, $0.01.

Cheap-tier models misformat regularly and self-referential content breaks the parser, so
on long queues this failure mode is a constant tax: each occurrence wastes the whole run
and requires a manual re-drive.

## Goal

A conformant answer that arrives without a *parseable* JSON envelope is recovered
automatically: the extractor finds any valid block regardless of backticks embedded in
string values, and when no block exists the runner makes a bounded corrective attempt
(and optionally a tier escalation) before flagging. Contract failures become rare,
visible (evented), and cheap — never an instant task death after otherwise-successful
work.

## Approach (suggested)

- Extractor first (cheapest, deterministic): see amendment (1). This alone would have
  saved this task's first drive.
- On a genuine `json is null` after a zero-exit run, re-invoke the subagent on the
  **same tier** with the prior output attached and a corrective instruction: the
  previous completion was missing the fenced ```json block required by the stage
  contract; reply with ONLY that block (derive it from the prior answer — do not redo
  the work). One cheap turn usually suffices since the reasoning already happened.
- Bound it: a `maxContractRetries` config (default 1–2), mirroring `maxStallRetries`
  loading in `RelayConfigLoader` (`OptionalInt`, default at `Defaults()`). Consider one
  final attempt on the next-higher tier when same-tier correction fails, then flag with
  the existing hint.
- Emit a `contract_retry` event (like stall retries; `RelayEvent` already carries
  `Attempt`/`StageNumber`/`Tier`) so drives/logs show the recovery. Use
  `RelayAttempt.Next` so retry traces get distinct `stage{n}-attempt{k}` dirs.
- Tests (the suite already fakes the runner — `ScriptedSubagentRunner` in
  `SubagentRunnerTestDoubles.cs`): (a) first output lacks the block, corrective call
  returns it → stage succeeds, one `contract_retry` evented; (b) corrective prompt
  contains the prior output and only-the-block instruction; (c) exhaustion still flags
  with `no valid fenced json block`; (d) `maxContractRetries: 0` preserves today's
  fail-fast behavior; plus the extractor cases from the amendment.
