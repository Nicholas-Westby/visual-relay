# Re-ask once when a stage's answer lacks a required contract key

A Research stage on `deepseek-flash` spent 32 turns reading gorilla/mux and
then answered without the required `findings` key. The runner re-asks once when
an answer holds no JSON at all, but an answer that parses and merely lacks a key
is deliberately not re-asked (the reader's note says one more turn is unlikely
to change it), so the stage was flagged, the task waited for an operator, and
`resume` later committed it without incident. The asymmetry is wrong: a re-ask
costs one tool-less turn on the cheap tier; a flag costs an operator's
attention and a resume. Extend the single re-ask to the missing-key case and
name the key in the message.

## Evidence

- Validation of 2026-09-11, mux: one Research stage on `deepseek-flash` used 32
  turns and answered without `findings`; the harness flagged the task; `resume`
  committed it. No harness fault was involved.
- 32 turns is a voluntary answer, not exhaustion: the config default is
  `maxTurns: 200` (`Configuration/RelayConfigLoader.Defaults.cs:26`, applied at
  `Execution/RelayDriver.Invocation.cs:27-29`), and exhaustion is its own
  outcome with an empty answer (`Agent/AgentTurnLoop.cs:136-137`,
  `AgentLoopOutcome.Exhausted`). A per-tier turn ceiling would not have touched
  this run. There is no cost ceiling anywhere (`grep -rn "costCeiling\|MaxCostUsd"
  src/` is empty) and no "answer now" nudge.

## Current state (researched at 5b85640b)

- `Execution/RelayStages.cs:10`: Research is stage 2, tier `cheap`, contract
  `{ "findings": string, "constraints": string[], "conventions"?: string[] }`.
  Required keys come from `StageContractReader.RequiredKeys`
  (`Agent/StageContractReader.cs:182-185`; a `?` before the colon marks optional).
- `StageContractReader.cs:101` accepts an object only when every required key is
  present; `:112-120` otherwise answers `the contract is missing the required key
  "findings"` with `Unparseable` left false. The design note at `:20-25` says a
  shape mismatch "one more turn is unlikely to change".
- `Agent/FirstPartySubagentRunner.Contract.cs:38` `if (!contract.Unparseable)
  return (result, contract);` so the re-ask (`ReAskAsync` `:62`, prompt
  `:106-118`, a tool-less `AgentTurnLoop(client, [], …)` at `:78` with
  `MaxTurns: 1` at `:84`) fires only for an answer with no JSON; a second failure
  flags (`:40-43`). `FirstPartySubagentRunner.cs:191-193` returns
  `IsValid: false` with the reader's error; `Execution/RelayDriver.cs:164-168`
  flags at once. `StageEscalation` never applies here (its consumers are stage
  11 and the review pair).
- Events: `contract_reask` (info) and `contract_repaired` (warn) at
  `Contract.cs:132,137`; the flag is `flagged` (error),
  `RelayDriver.Events.cs:149-151`.
- The current behavior is pinned by
  `FirstPartySubagentRunnerContractTests.AMissingContractKey_IsNotReAsked`
  (`:143`): `{"summary":"no options here"}` gives `IsValid == false` after
  exactly one wire call.

## Prescribed approach

1. `StageContractResult` gains `IReadOnlyList<string> MissingKeys` (every absent
   required key, in contract order, empty when the object fits or nothing
   parsed) and `bool NeedsReAsk => Unparseable || MissingKeys.Count > 0`. The
   error text keeps naming the first key.
2. `ResolveContractAsync` re-asks when `NeedsReAsk`. For the missing-key case
   the re-ask message is: "Your answer parsed, but its JSON block lacks the
   required key(s): `findings`. Reply with ONE fenced json block containing every
   key of this contract: <contract>. Keep what you already wrote; do not call
   tools." The unparseable case keeps its current message. Everything else about
   the re-ask stays: one tool-less turn, `MaxTurns: 1`, the same model and
   budget, exactly one re-ask per stage attempt whichever reason triggered it. A
   second incomplete answer flags with the existing error text.
3. The `contract_reask` event's data gains `reason` (`unparseable` or
   `missing-key`) and `keys` (comma-joined, empty for unparseable).
4. Rewrite the reader's note at `StageContractReader.cs:20-25`: the shape
   mismatch now buys the same single re-ask, because a flag is dearer than a
   turn.
5. `docs/relay-artifacts.md` gains a short "Runner events" table with
   `contract_repaired` and `contract_reask` (level, data, when), beside the
   stage-5 table.

## Tests

`tests/VisualRelay.Tests/FirstPartySubagentRunnerContractTests.cs` (replace
`AMissingContractKey_IsNotReAsked`):

- `AMissingContractKey_IsReAskedOnceWithTheKeyNamed`: two wire calls; the last
  user message of the second request names `findings` and contains the
  contract; a complete second answer makes the result valid.
- `ASecondIncompleteAnswer_FlagsWithoutAnotherReAsk`: two wire calls, then
  `IsValid == false` with the missing-key error.
- `AnOptionalKey_IsNeverAskedFor`: an answer lacking only `conventions` is
  valid after one call.
- `TheMissingKeyReAsk_CountsTowardTheStage`: the report's `llm_calls` is 2.
- `TheReAskEvent_NamesItsReason`: `contract_reask` carries `reason=missing-key`
  and `keys=findings`; the unparseable path carries `reason=unparseable`.

`StageContractReaderTests`: `MissingKeys` lists every absent required key in
contract order; a parsed object with all keys has an empty list; an unparseable
answer has `Unparseable` true and an empty list.

## Verification (through the control API)

A live model cannot be made to drop a key on demand, so the unit tests carry the
proof of the mechanism; the run below proves no regression. Clone gorilla/mux as
`/Users/admin/Dev/vr-work/mux.prep.md` describes, then `open-folder`,
`bootstrap`, patch `testCmd` to `go test -count=1 ./...`, `refresh`, one
`create-task` per task, `run-all`, and poll `/state` until `isBusy` is false.
In every `.relay/<task>/run.log` count `contract_reask` events and their
`reason`; a stage that was re-asked must have completed (no `flagged` event for
that stage). Put the counts and the served model in the commit body.

## Out of scope

A per-tier turn or cost ceiling (no evidence it would have helped: 32 of 200
turns were used); tier escalation on a contract failure; a "final turn" nudge
(exhaustion is a separate outcome that did not occur here).

## Rejected alternatives

- A turn ceiling for the cheap tier: the model answered at 32 of 200; any
  ceiling low enough to matter here would cut short a Research stage that was
  reading the repository exactly as asked.
- Escalating to the balanced tier on a missing key: a shape error is not a
  capability error, and the dearer tier is not known to be better at shapes.
- Leaving it as is: a flag for a fixable shape costs an operator's resume every
  time it happens.
