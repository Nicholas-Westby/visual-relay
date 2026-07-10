# Report Review and Visual-review timing/status independently, not at the pair barrier

Stages 7 (Review, frontier) and 8 (Visual-review, vision) run concurrently as a pair
(`src/VisualRelay.Core/Execution/RelayDriver.ReviewPair.cs`). Observed defect from a real run
(add-status-pill-to-featured-project on an Astro site, 2026-07-10, run 20260710173722):

- run.log: `s8/vision stage_start` at 17:56:02, `s8 stage_done … time=16m 50s` at 18:13:13 —
  but the stage-8 vision report artifact (`stage8-attempt1.report.json`) is timestamped
  17:56:48, i.e. the vision call finished in ~46 seconds and cost $0.0031 (one short call).
- `s7 stage_done time=17m 11s` was published at the same instant (18:13:13) — the pair's barrier.
- Consequences: the UI stage tile showed Visual-review as 16m 50s (inheriting the frontier
  review's wall clock — misleading cost/latency signal for a stage the user is evaluating), and
  live `/state` showed BOTH stages as Running for the whole 17 minutes even though stage 8 had
  finished its work in under a minute — an operator watching the board cannot tell a slow vision
  call from a long concurrent review.

## What to build

1. **Per-stage measurement.** Time each of the pair's stages around its OWN work (including only
   what that stage actually does; the internal visual-triage gate keeps its current attribution
   but must not silently inflate stage 8's number — if triage time is folded in anywhere, log it
   as its own event or a labeled component). `timeSeconds`/cost on `stage_done` reflect the
   stage's own window, not the barrier.
2. **Per-stage completion signaling.** Publish each stage's `stage_done` event and flip its
   status to Done/Skipped/Flagged when THAT stage finishes, not when the pair joins. The pair
   barrier itself is unchanged — stage 9 must still wait for both — but observability becomes
   truthful: after the fast stage completes, `/state.stages` shows it Done while its sibling is
   still Running.
3. Downstream consumers of the events (status.json, ledger, UI tiles, drain summary) need no
   schema change — they already render whatever `stage_done` carries; verify none of them assumes
   the two events arrive together or in a fixed order (7-then-8 must not be relied upon).
4. Failure paths preserved exactly: a flagged/errored sibling still fails the pair per current
   semantics; cancellation still tears both down.

## Tests (red first)

Extend the existing ReviewPair test family:
- Fast-visual/slow-review pair (inject stage runners with controlled delays): stage-8
  `stage_done` fires at its own completion with its own duration (assert its `timeSeconds` is
  close to the injected fast delay, not the slow sibling's), and the stage status snapshot after
  the fast stage completes shows 8 Done + 7 Running.
- Symmetric case (slow visual, fast review) for stage 7.
- Barrier regression: stage 9 does not start until both are done (existing coverage stays green).
- Event-order tolerance: consumers handle 8-done-before-7-done (drain summary/status.json tests).

## Verification

- `./test.sh` fully green including the new tests.
