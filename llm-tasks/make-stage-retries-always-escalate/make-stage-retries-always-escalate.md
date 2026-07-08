# Every Stage Retry Must Escalate — No Repeats at the Same Config, Max 3 Attempts

Today a failing stage can burn up to **9 attempts**: the in-process retry loop
(`SwivalSubagentRunner.RunAsync`, see `ProcessRunners.RunAsync.cs`) gives each escalation rung
a same-config retry pool — `1 + MaxStallRetries(2) = 3` attempts at the identical tier and
turn budget for stall/crash/nonzero-exit failures (contract failures use a separate
`1 + MaxContractRetries(1) = 2` pool with a corrective prompt) — and only then climbs one rung
via `TryEscalateAsync`, which **resets the pools**, up to `MaxStageFailures (3)` runs total.
Observed in practice (2026-07-07, `add-parallel-visual-review-stage` stage 6): three identical
balanced@200 attempts of 35–43 minutes each, all dying the same way (turn exhaustion,
exit 2), before the first escalation — two of those three attempts were predictable waste.

**New policy (owner decision): a retryable failure always escalates.** Attempt index == run
index. Total attempts = `MaxStageFailures` = 3. For a balanced-base stage:
`balanced@200 → frontier@400 → frontier@800`. Never re-run the same **config**, where config =
(tier, max-turns) pair.

## What to build

1. **Remove the same-config retry pools.** In the `RunAsync` loop, on any retryable failure
   (stall kill, nonzero exit, contract/shape reject), go straight to `TryEscalateAsync`; when
   escalation is exhausted, fail the stage. Delete `stallRetriesLeft` / `contractRetriesLeft`
   and their reset-on-escalation. The ladder math itself (`StageEscalation.TierForRun` /
   `TurnsForRun` / `RunMultiplier`) is untouched — only the caller's loop changes.
2. **Retire the config knobs.** Remove `MaxStallRetries` and `MaxContractRetries` from
   `RelayConfig`, the loader defaults/parsing (`maxStallRetries` / `maxContractRetries` keys),
   and any consumers/tests. The loader ignores unknown keys, so stale configs stay loadable.
   (`MaxStageFailures` stays — it is now literally "max attempts".)
3. **Carry the corrective context across escalation.** Today a contract failure retries the
   same tier with a corrective prompt describing the malformed output, and escalation resets
   that context (`correctivePriorOutput = null`). With same-tier retries gone, the corrective
   prompt would never be seen. New behavior: on a contract-failure escalation, feed the
   corrective diagnostic into the escalated attempt's prompt (the higher tier should know what
   the previous output got wrong). Stall/crash escalations carry nothing, as today.
4. **Enforce "no repeats" at the ladder's edge.** The no-repeat rule has one corner: under the
   flat 10× boost, turn doubling is suppressed (`RunMultiplier` returns 1), so a
   frontier-base boosted stage would compute run 2 = run 1's exact config (frontier@2000).
   When the next rung's (tier, turns) equals the current config, treat the ladder as
   **exhausted** — fail the stage rather than re-running an identical config. State this in a
   comment; it means a boosted frontier-base stage gets exactly one attempt, which is the
   correct reading of the policy (repeating the identical config is what the owner rejected).
5. **Leave the exceptions alone.** Hard infra aborts (absolute-ceiling hit, socket wedge)
   already never escalate — unchanged. The driver's external fix-verify loop escalates via the
   same `StageEscalation` ladder, one config per run — verify it already conforms to
   no-repeats (it should, via `TierForRun`/`TurnsForRun` per run) and leave it as-is; if it
   has any same-config re-run path, apply the same rule there.
6. **Update dependents.** Attempt numbering now equals run numbering: check artifact naming
   (`stage{n}-attempt{k}` traces, killed-output files), the `stage_escalated` Run-Log message
   (`DescribeTransition` wording still fits: "run 2/3"), any UI attempt display, and every
   test that asserts retry counts, attempt sequences, or the escalation cadence (escalation
   tests, watchdog escalation paths, config-loader tests for the removed keys).
7. **Tests** (adjust existing + add):
   - stall/crash failure at run 1 → next attempt is run 2's config (tier stepped, turns
     doubled), no same-config attempt in between;
   - contract failure → escalated attempt's prompt contains the corrective diagnostic;
   - three failing runs → stage fails after exactly 3 attempts with distinct configs;
   - flat-boost frontier-base stage → exactly 1 attempt (dedupe exhaustion path);
   - hard aborts still bypass escalation entirely;
   - loader: stale configs containing the removed keys still load.

## Done when

- A stage that keeps failing produces exactly `MaxStageFailures` attempts, each with a
  distinct (tier, max-turns) config, e.g. balanced@200 → frontier@400 → frontier@800, visible
  in the Run Log as consecutive `stage_escalated` transitions with no same-config repeats.
- The corrective-prompt path demonstrably reaches the escalated attempt on contract failures.
- No references to `MaxStallRetries` / `MaxContractRetries` remain anywhere.
- Full suite green; `./visual-relay check` passes.

## Guardrails

- Ladder math in `StageEscalation` stays the single source of truth — no second tier/turn
  table anywhere.
- No changes to watchdog detection, inactivity windows, ceilings, or the 10× boost semantics
  beyond the dedupe-exhaustion rule in item 4.
- Purely mechanical control flow — no model/prompt behavior changes except the corrective
  carry in item 3; nothing repo- or stack-specific.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs; touched files
  stay under the 300-line guard.
