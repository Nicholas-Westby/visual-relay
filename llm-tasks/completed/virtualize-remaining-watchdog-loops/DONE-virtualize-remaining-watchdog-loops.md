# Virtualize the remaining watchdog WaitAsync loop tests

The test `WaitAsync_BurstThenTotalSilence_FiresAtInactivityDeadline` was sped
up from ~26 s to sub-second by replacing its 120-iteration `Task.Yield()` pump
with a single time-jump plus a few `Task.Delay(1)` real ticks. Six sibling
tests across two partial-class files still use the old pump-every-N-ms pattern
and suffer the same thread-pool contention under `maxParallelThreads: 2.0x`.
Collectively they account for ~30 s of the wall-time tail (see the per-test
durations in
`llm-tasks/completed/speed-up-automated-tests-july-17/timings-baseline.txt`).

## Measured cost

From the baseline (full-suite, 2.0×-worker, 92 s wall time):

In `ActivityWatchdogDecisionTests.WaitAsync.cs`:
- `WaitAsync_CpuPulseMasksDeadline_SocketWedgeStillFires`       ~6–8 s
- `WaitAsync_BusySubtree_NotKilled_EvenWithSocketAndSilence`    ~6–8 s
- `WaitAsync_BurstyAgent_IdleSampleButRecentCpuBurst_NotKilled` ~6–8 s
- `WaitAsync_SustainedIdlePlusSocket_StillFiresSocketWedge`     ~6–8 s

In `ActivityWatchdogDecisionTests.WaitAsync.OutputSilence.cs`:
- `WaitAsync_CpuPulseMasksDeadline_OutputSilenceFires`          ~6–8 s
- `WaitAsync_ContinuousRealOutput_OutputSilenceGateNotFired`    ~4–6 s

All six reside in the `ActivityWatchdogDecisionTests` partial class.

## Prescribed approach

Each test uses a structure like:

```csharp
var watchdogTask = watchdog.WaitAsync(…);
for (var i = 0; i < 60 && !watchdogTask.IsCompleted; i++)
{
    // feed one pulse per step
    time.Advance(TimeSpan.FromMilliseconds(50));
    await Task.Yield();
}
```

Replace the `for` loop with a two-phase strategy identical to what was done in
`WaitAsync_BurstThenTotalSilence_FiresAtInactivityDeadline`:

1. **Phase 1 — pulse loop.** Feed every required pulse in a tight loop using
   `time.Advance()` but *without* a real yield between advances — the
   `ManualTimeProvider` fires timer callbacks synchronously during `Advance`,
   so the watchdog's internal state updates inline. No `Task.Yield()` needed
   yet.

2. **Phase 2 — deadline jump.** After the last pulse, advance the clock well
   past the remaining inactivity/output-silence deadline with a single large
   `time.Advance()`, then pump the async state machine with at most 5
   `await Task.Delay(1)` calls — the same pattern proven in the fixed test.

The net effect: each test drops from ~60 `Task.Yield` iterations (contention-
inflated) to a handful of `Task.Delay(1)` real-time ticks, reducing per-test
wall time from ~6–8 s to well under 100 ms.

### Steps

1. Open `tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.WaitAsync.cs`.
2. For each of the four loop-based tests (everything that uses
   `for (var i = 0; i < 60`), restructure into phase-1 (pulse advances without
   yield) and phase-2 (deadline jump + ~5 `await Task.Delay(1)`).
3. Open `tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.WaitAsync.OutputSilence.cs`.
4. For each of the two loop-based tests (same pattern), restructure identically.
5. Run the full suite to green. Expected: same assertions pass with unchanged
   coverage.

### Guardrails

- Do not change any assertion — same pass/fail criteria.
- The `ManualTimeProvider` fires timers synchronously; the pulse loop must not
  introduce a real-time yield until phase 2.
- Keep the final `await watchdogTask` and the `IsCancellationRequested` check
  exactly as they are today.
- Coverage rules apply: no test may be deleted, skipped, or weakened. Any move
  or merge requires a name-by-name mapping of every original test to its new
  location.

## Expected savings

Per test: ~8 s → 0.1 s (98 % reduction). Collective saving across all six
Watchdog-loop tests: ~30 s of the wall-time tail removed.

## Commit-message evidence

Measure before and after while implementing, then put one filled-in evidence
bullet in the commit message body, following the attached
`commit-message-evidence.md`. Never pre-fill that bullet — numbers are measured
at implementation time and go into the eventual commit message, nowhere else.
