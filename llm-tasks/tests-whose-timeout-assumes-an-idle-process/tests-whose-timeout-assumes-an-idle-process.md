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

`95ca6494` raises the floor to 8x processor count in the test module initializer. It was
measured, 18 runs per condition on Windows and 6 per condition on macOS, with the columns
declared usable from within-condition noise BEFORE any comparison and p50 held back as a
control. Result: a **small, consistent, directional improvement and no harm**.

  Windows, usable columns only, control clean:
    p50   1.00x   all values tied, test undefined — the control did not move
    >1s   0.98x   2% fewer tests over one second
    >2s   0.93x   7% fewer tests over two seconds
  Every column that FAILED the noise rule pointed the same way: 0.88x to 0.95x.
  macOS, on a machine with no starvation to relieve, found no degradation in any
  column — the predicted cost of a raised floor did not appear.

What that does NOT establish, and must not be read as: it does not explain the flakes.
A 2-7% shift in how many tests cross a one- or two-second line is nowhere near enough to
turn 3 failures in 18 runs into 0. The failure tally was uninformative by prior agreement
(3 failures forming 2 clusters against 0, p=0.486), so the flake question is still open
and this change is not its answer.

Read the significance as direction, not as odds: the 18 runs per side are consecutive
rather than independent, and the observed failures clustered, so the same autocorrelation
inflates those z-scores.

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

## If you build an instrument to measure this

Five instruments were written for this question in one afternoon and all five were
wrong at least once. Four failed toward silence or a plausible wrong answer:

  a relative output path         wrote nowhere; caught by an absence of output
  a sort on undated filenames    right data filed under the wrong condition
  an ordering rule on timestamps the matched control ran LAST, so "later means after"
                                 inverted every sample while looking perfectly sorted
  a glob delete on an empty dir  aborted the whole run with exit 0 and no output

The usual defences catch those: is the output the shape you expected before you ran it;
verify the property rather than the commit; read which tests failed, not how many.

The fifth failed the other way and no defence above would have caught it. A rank test
with no tie handling reported the control as moving at z=+5.13 when all 36 values were
identical — a catastrophic-looking control failure produced by arithmetic, which would
have caused a clean result to be discarded as contaminated. It was caught only because
z=+5.13 and a ratio of exactly 1.00x cannot both be true.

So add this check: **when an instrument produces an alarming result, test it against the
other numbers that same instrument produced before acting on it.** A control that fails
while the quantity it controls for is exactly zero is not a control failure, it is an
arithmetic failure. Every other guard here defends against believing a result you want;
this one defends against discarding a result you do not.

## Rejected alternatives

- **Raising the 5s deadlines.** Moves the race. See above.
- **Rewriting the polls as event waits.** Correct by the repo's own `TestWaits` doctrine
  and insufficient here: the event cannot fire because the thread that would raise it is
  queued. Both of the diagnoses that suggested this were wrong, and both came with a
  convenient fix, which is a reason to distrust them rather than to adopt them.
