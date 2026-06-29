# Harness/UI: escalate tier + turns on stage failure (max 3 runs/stage), cumulative turn display

## Goal
When a stage fails, retry it with an **escalated** model tier and turn budget, up to **two
escalations (3 runs total per stage)**, then flag. This replaces today's narrow escalation
(a one-shot tier bump that fires only on malformed-output failures and never changes turns)
with the intended general behavior.

## Target behavior (precise)
- **Trigger:** on a stage **failure** (the attempt did not succeed — verify/test red, invalid/
  contract result, nonzero exit, etc.), escalate and re-run, until success or **3 runs** are
  used, then `FlagAsync`. (Decide whether hard infra aborts — absolute-ceiling kill, socket
  wedge — count as escalatable; lean toward hard-failing those and escalating the rest.)
- **Each escalation does two things:**
  1. **Tier +1 step** up `cheap → balanced → frontier`, **capped at frontier** (if already
     frontier, stays frontier).
  2. **Turns ×2** (double the stage's max-turns budget).
- **Turn ladder** (base = `config.MaxTurns`, default 200): run1 = base, run2 = 2×base, run3 =
  4×base → e.g. **200 / 400 / 800**.
- **Tier examples** (run1 = the stage's DEFAULT tier from `RelayStages.cs`):
  - default cheap → `cheap 200 → balanced 400 → frontier 800`
  - default balanced → `balanced 200 → frontier 400 → frontier 800`
  - default frontier → `frontier 200 → frontier 400 → frontier 800`
- **10× turn-boost mode** (task in `BoostTurnsTaskIds`): the per-escalation **turn doubling does
  NOT occur** — turns stay flat at 10×base (e.g. 2000) across all runs; the **tier still
  escalates** per the ladder.
- **Defaults preserved:** the per-stage tiers currently in `RelayStages.cs` remain the run-1
  defaults (do not change them).

## Current state to generalize (from investigation — confirm before editing)
- Sole escalation today: `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs:256-294` —
  one-shot (`escalationUsed` bool), gated on `MaxContractRetries>0`, triggered ONLY by a
  contract/output-validity failure, changing ONLY `Tier`. `NextTier`
  (`ProcessRunners.Helpers.cs:150-155`) already does cheap→balanced→frontier capped at frontier.
  Generalize: any failure, add turn-doubling, allow up to 2 escalations.
- Turns today: flat ×10 via `SaturatingBoost` (`RelayDriver.VerifyFix.cs:13,216-217`,
  `Bootstrap.cs:159-163`), gated on manual `BoostTurnsTaskIds`. Keep ×10 as the flat
  "no-doubling" mode; add the ×2 doubling for non-10× runs.
- Fix-verify loop: `RelayDriver.VerifyFix.cs:44-58` loops up to `MaxVerifyLoops` (default 5) at
  a FIXED tier/turns. **Reconcile** to the 3-run escalation model (each fix-verify attempt is an
  escalation run; cap at 3, escalating tier+turns each time). Decide the fate of `MaxVerifyLoops`
  (likely subsumed) and of the non-convergence early-flag guard (`IsNonConvergent`) — a higher
  tier may produce a different result, so don't early-flag before escalations are spent.
- `MaxStageFailures` (default **3**, `RelayConfig.cs:10`, `RelayConfigLoader.cs:207`) is defined
  but **unused** — a natural home for the "3 runs" cap (or add an explicit `MaxEscalations=2`).
- Watchdog ceiling: ensure the absolute ceiling accommodates the escalated (doubled) turn budget
  so an 800-turn run isn't reaped prematurely.
- Apply uniformly: the central `RunAsync` is the single escalation site; all four call sites pass
  a stage-pinned tier.

## UI
- **Cumulative turns:** wherever turns are shown (stage cards, activity), show the **cumulative**
  turns summed across all escalation runs of the stage — NOT just the latest run. Same
  cumulative-across-retries family as `harness-fix-stage-and-task-timers` (which fixes the
  analogous per-attempt *timer* reset); keep the two consistent.
- **Run Log:** each escalation must be **clearly shown in the Run Log tab** — a labeled entry,
  e.g. `Stage 10 Fix-verify escalated (run 2/3): tier balanced→frontier, max-turns 200→400`.

## Done when
- A failing stage escalates tier + turns per the ladder, max 3 runs, then flags; the 3 examples
  hold; `RelayStages.cs` defaults are the run-1 tiers; frontier is the cap.
- 10× mode: tier escalates, turns stay flat (no doubling).
- UI shows cumulative turns; the Run Log clearly logs each escalation transition.
- Tests cover: the ladder for each default tier, the frontier cap, the 3-run limit, the 10×
  interaction, cumulative-turn display, and the run-log escalation entries.
- General-purpose (no test-framework/VR specifics in the engine); `./visual-relay check` green;
  suite green under nono; Conventional Commits.
