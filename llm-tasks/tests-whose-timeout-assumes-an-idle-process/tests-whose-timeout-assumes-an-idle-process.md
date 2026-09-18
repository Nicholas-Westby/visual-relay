# Tests whose timeout assumes the process is not saturated

About one Windows full-suite run in five fails a test that is not broken. Five tests
are involved, reaching three helpers, and all of them fail the same way: a fixed
timeout expires while the work it was waiting for has already happened.

The work is never slow. The NOTIFICATION is slow, because continuations queue behind
blocked thread-pool threads in a process running ~5,090 tests across 12 threads on
6 cores. Isolation never reproduces any of it, because isolation removes the cause.

## Evidence

Windows PC, 2026-09-18, measured on the machine. macOS has seen one occurrence all
day and cannot reproduce them.

- Rate: **4 of 18** full-suite runs failed at least one test, from a pool of five:
  `SettingsModalUiTests.ClickingSettingsCogTwice` (twice),
  `HermeticFastSuiteTests.NonLoopbackPlainHttpHost_IsRefused`,
  `HermeticFastSuiteTests.LoopbackRequest_IsAllowedThrough`,
  `CliNonoGateTests.Launch_SandboxEnabled_NonoPresent_PullsNoProfilePack`.
  A sixth, `ProcessCaptureConcurrencyTests`, occurred once on macOS.

- The deadline is never in the test. It is in a helper, which is why the failures look
  unrelated and why grepping the tests finds nothing:

      SettingsTestHelpers.OpenSettings          5s wall-clock poll for a window
      HermeticHttpHandler.CreateClient          5s HttpClient timeout
      CliHarness.RunAsync                      30s token around a spawned process

- The mechanism, measured directly with an instrumented probe:

      child process exited at   127ms, the await returned at 5,288ms  (stall 5,161ms)
      acceptor accepted at      121ms, the request completed at 5,085ms
      71 work items pending against 10 pool threads

  Nothing is slow to happen; everything is slow to be noticed. A request at 5,085ms
  against a budget of exactly 5,000ms is the failure caught in the act.

- `IsolatedCollectionDefinition` already records the same mechanism from an earlier
  encounter: a child gone 0.55s in was reported back at 6.1s in a parallel full run.

## Current state

`ThreadPool.SetMinThreads` was never called anywhere. Below the minimum the pool
creates threads on demand; above it, it adds roughly two per second while it
hill-climbs, so once every thread is blocked the work that would release them waits
on that trickle.

`95ca6494` raises the floor to 8x processor count in the test module initializer.
Whether it fixes this is **UNRESOLVED**, and the spec should not be read as saying it
does. What is established:

- no measured harm, on two machines and after an earlier harm finding was withdrawn
  (it used a confounded baseline, and an independent test on the arm chosen to detect
  the cost found no degradation in any column);
- no proven benefit: the failure tally is uninformative under the agreed rule that
  clustered failures count as one episode, and the quantile comparison was at or
  beyond the resolution of 18 runs per condition.

## Prescribed approach

Decide whether the floor helps, then fix what it does not cover. The two are separate.

1. The floor is a supply-side remedy and cheap. If a later measurement shows it helps,
   keep it; if it shows harm, remove it. It should not be defended on the grounds that
   nobody has disproved it.
2. `Isolated` (`DisableParallelization = true`) is the demand-side remedy and already
   exists for exactly this. `CliNonoGateTests` is a candidate: only two classes use the
   spawning harness, so moving them is bounded. It costs serial tail time, so measure
   it against the 90s budget rather than assuming it is free.
3. `SettingsModalUiTests` cannot take that hatch: 66 classes share the `Headless`
   collection, and moving them all would serialise the UI suite against everything.
4. Do NOT widen the deadlines. The measured request was 85ms over its budget; a bigger
   number moves the race rather than removing it.

## Tests

Whatever is done, the acceptance test is the rate on Windows, and it needs enough runs
to see it: at 22% before, distinguishing "fixed" from "less often" needs more than the
18 per condition tried here.

## What was measured and should not be re-derived

- Isolation runs prove nothing about these tests. 0 of 10 in isolation was recorded for
  every one of them while the rate under the full suite stayed at 22%.
- External machine load is not the variable. A test hammered from outside while running
  alone does not fail; the same test inside the full suite does.
- The suite's own timing statistics are mostly unusable at this sample size. On macOS,
  across six identical runs: p95 varied 58%, p99 42%, the >5s count 60% — while p50
  varied 5%. Any comparison on the noisy columns is sampling, not signal.
- p50 is a useful control precisely because it cannot move: 57% of tests finish in under
  a millisecond and never touch the pool.

## Rejected alternatives

- **Raising the 5s deadlines.** Moves the race. See above.
- **Rewriting the polls as event waits.** Correct by the repo's own `TestWaits` doctrine
  and insufficient here: the event cannot fire because the thread that would raise it is
  queued. Both of the diagnoses that suggested this were wrong, and both came with a
  convenient fix, which is a reason to distrust them rather than to adopt them.
