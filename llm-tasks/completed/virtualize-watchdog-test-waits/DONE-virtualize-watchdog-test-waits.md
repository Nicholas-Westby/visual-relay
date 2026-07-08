# Virtualize the Remaining Real-Time Watchdog Tests ("Part B")

The watchdog test class (`SwivalSubagentRunnerWatchdogTests`, ~19 facts spread across five
partial files: base + `.ActivityWatchdog.cs`, `.CpuPulse.cs`, `.NonzeroExit.cs`,
`.TierWindows.cs`) spawns **real bash child processes** (fake-swival scripts like
`exec tail -f /dev/null`, pulse loops) and then **really waits** through watchdog inactivity
windows of 3–6 seconds plus ~5s kill backstops, per test. The class sits in
`[Collection("Watchdog")]`, deliberately serialized so CPU contention can't skew its timing
assertions — which makes it one single-file chain of **~100 seconds** of mostly wall-clock
waiting. Measured 2026-07-07 (from `test-logs/*.trx`): it is the largest single serial block
in the suite and becomes the critical path once test-collection parallelism is widened.

Half the migration already happened ("Part A"): newer facts in
`SwivalSubagentRunnerWatchdogTests.TierWindows.cs` use a sleep-free pattern — names like
`DecideOutcome_PulsesResetSilence_DisarmedFarPastOldFlatCap` — that call the watchdog's
**decision seam** directly with synthetic clock/pulse inputs ("given this much silence and
these pulses, what would you decide?") instead of spawning processes and waiting in real time.
One formerly ~16s real-wait fact was already replaced this way. `RealSleepGuardTests.cs`
documents the plan explicitly: its enforcing gate is `[Fact(Skip = …)]` "until Part B" — this
task is Part B.

## What to build (TDD-first)

1. **Inventory.** List every remaining fact in the Watchdog collection (all five partial
   files) plus the sibling real-process timing classes `ActivityWatchdogSocketWedgeTests` /
   `ActivityWatchdogSocketWedgeTests.SustainedIdle.cs`, with its current duration (from the
   newest `.trx` in `test-logs/`) and the mechanism it exercises (inactivity window, CPU
   pulse, nonzero exit, socket wedge, kill backstop, graceful-stop flush…).
2. **Virtualize by default.** For each fact, re-express the behavior as a decision-seam test in
   the established `DecideOutcome_*` style: drive the watchdog's decision function with
   synthetic timestamps/pulse sources and assert the outcome — no child processes, no real
   waiting. If the seam can't express some behavior (e.g. actual SIGKILL delivery, actual
   stderr pumping), that behavior belongs to a smoke test (next item), or the seam needs a
   small, behavior-preserving extension — prefer extending the seam over keeping real waits.
3. **Keep 2–3 end-to-end smoke tests, no more.** Real process spawn + real kill path, CPU-pulse
   detection on a live child, and the socket-wedge scenario. These stay in the serialized
   Watchdog collection; everything else leaves the collection (virtualized tests have no
   contention sensitivity and can parallelize freely).
4. **Enable the guard as a permanent regression tripwire.** Un-skip the enforcing gate in
   `RealSleepGuardTests.cs` — and make it the automated test that **fails whenever a real
   delay is (re)introduced in test code**, so this problem cannot quietly resurface as the
   suite grows. Concretely:
   - First read what the gate actually scans and how its docs intend it to be scoped.
   - Prefer the **widest enforceable scope** (all of `tests/`), with an explicit, commented
     **allowlist** for the legitimate exceptions — the 2–3 real-process smoke tests kept by
     this task, and any genuine process-wait patterns in git-integration tests. Each allowlist
     entry gets a one-line justification. Do NOT solve false positives by narrowing the
     scanned scope: a new `Thread.Sleep`/`Task.Delay`-style real wait added anywhere in test
     code next month must fail the guard by default, forcing the author to either virtualize
     it or consciously allowlist it.
   - If the existing gate's design genuinely cannot cover the whole suite, cover at minimum
     every watchdog/timing test file with it AND add a suite-wide static-scan convention in
     the repo's established guard idiom (see how `SplitGuardVerificationTests` polices
     conventions) so the default-fail behavior still holds everywhere.
   - Never weaken the gate's assertion to make it pass.
5. **Coverage parity, argued not asserted.** In the final summary, map every removed real-wait
   fact to the decision-seam fact(s) that now cover the same behavior, one line each. A fact
   with no successor must be one of the 2–3 smokes.

## Done when

- The Watchdog collection's summed runtime drops from ~100s to ≤20s (verify from a fresh
  `.trx`), with at most 2–3 real-process smoke tests remaining in it.
- The `RealSleepGuardTests` gate runs un-skipped and passes, and demonstrably fails when a
  real delay is introduced in test code outside the commented allowlist (prove it in
  development by temporarily adding a `Task.Delay` fact, watching the gate fail, then removing
  it — mention this check in the summary).
- The parity map (old fact → new fact) is in the summary; no watchdog behavior lost.
- Full suite passes 3 consecutive runs; `./visual-relay check` passes.

## Guardrails

- **Production watchdog code changes only if a seam extension is genuinely required**, and then
  strictly behavior-preserving (the decision logic must not change); everything else is
  tests-only.
- Do not delete a real-wait test without a parity entry; do not relax any timing assertion to
  make virtualization easier.
- The 2–3 smokes stay serialized in `[Collection("Watchdog")]`; do not remove the collection.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); files under the 300-line
  guard (the partial-file layout exists for this — keep using it).
