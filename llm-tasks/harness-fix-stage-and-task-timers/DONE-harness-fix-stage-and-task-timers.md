# Harness/UI: stage timer resets on retry, and overall task time doesn't match the sum of stages

Two related timer-display defects (same family as the already-fixed `fix-stage-timer` and
`fix-queue-row-elapsed`). Fix both.

## Problem A — the stage timer RESETS on each retry/escalation
A stage that runs multiple attempts (notably **Fix-verify**, stage 10) shows only the
**current attempt's** elapsed, not the cumulative time the stage has spent. Each attempt
re-emits a `stage_start` event, and the GUI re-anchors the stage's running-since on **every**
`stage_start` (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs:88-92` —
`stage.MarkRunning(relayEvent.Timestamp)`), so the clock restarts each iteration.

**Evidence:** in one run, stage 10 emitted `stage_start` at `17:15:55`, `17:28:21`, `17:36:21`
(attempts 1/2/3), and the card displayed "Running 8s" while the stage had actually been running
~35+ min. Misleads the operator about whether a stage is stuck.

**Fix:** anchor a retried stage's timer to its **first** start (accumulate across attempts), not
re-anchored on each retry's `stage_start`. (E.g. only set the start time if the stage isn't
already running, or sum per-attempt durations.) Keep the existing task-switch persistence from
`fix-stage-timer`.

## Problem B — overall/main task time ≫ sum of the per-stage times
The queue-row "overall" task time does not reconcile with the displayed per-stage times.

**Evidence (screenshot):** `harness-revert-split-verify` showed **77m 39s** in the queue row,
while the 11 stage cards sum to **~18m**. The gap is two compounding causes:
1. **Un-summed retries** (Problem A): Fix-verify's ~35 min across attempts shows as seconds.
2. **Queue-wait counted as task time:** in a Run All drain the overall timer runs from the
   task's *planning start*, but a task can sit idle for a long time AFTER planning while ANOTHER
   task executes. Here the revert planned at ~15:58 UTC, then waited ~59 min while the
   inspect-code task executed (stages 5-11), then started its own execution at ~17:01 — and that
   idle wait is counted in its "overall" time but attributed to no stage.

**Fix:** decide and implement consistent semantics so the numbers reconcile — e.g.:
- Make "overall task time" = sum of the task's own stage times (active work), excluding the
  idle queue-wait while other tasks run; OR
- Keep wall-clock-from-planning but **decompose** it visibly (active vs queued/inter-stage) so
  77m vs 18m is explainable rather than contradictory.
Pick the intended meaning of the queue-row timer and make the stage-sum and the overall figure
agree (or clearly add up).

## Done when
- A retried stage (Fix-verify) shows cumulative stage time across attempts, not a per-attempt reset.
- The overall task time reconciles with the stage times (sum, or a clear active-vs-wait
  breakdown); no large unexplained gap.
- Headless-UI tests cover both (a retried-stage timer; an overall-vs-sum reconciliation).
- General-purpose (no VR-repo specifics); `./visual-relay check` green; Conventional Commit.
