# Virtualize BackendLifecycle test pump loops

Two `BackendLifecycleStatusTests` tests use a `while (!task.IsCompleted)` pump
loop that advances virtual time in 1-second steps with `Task.Yield()` between
each — the same pattern that was removed from the watchdog
`WaitAsync_BurstThenTotalSilence` test. The `ManualTimeProvider` already fires
timers synchronously on `Advance`, so the yield between steps is wasted real
time.

## Measured cost

From the baseline:
- `BackendLifecycleStatusTests.Start_RemovesLegacyRepoLocalState` — 33.2 s
- `BackendLifecycleStatusTests.Start_MissingToolchain_LogsRemediation_Exit1` — 10.8 s

Together they contribute ~44 s to the wall-time tail under the 2.0× parallelism
budget. These two are the biggest non-pipeline, non-guard tests.

(See `llm-tasks/completed/speed-up-automated-tests-july-17/timings-baseline.txt`.)

## Prescribed approach

Both tests follow an identical structure:

```csharp
var task = lifecycle.StartAsync(timeProvider: time);
while (!task.IsCompleted)
{
    time.Advance(TimeSpan.FromSeconds(1));
    await Task.Yield();
}
await task;
```

Replace with a single large advance past the `ReadyTimeout` plus a few
`await Task.Delay(1)` ticks for the async state machine — the exact same
two-phase fix applied to `WaitAsync_BurstThenTotalSilence`:

```csharp
var task = lifecycle.StartAsync(timeProvider: time);

// Advance time well past the ReadyTimeout (50 ms in the test).
// ManualTimeProvider fires the internal Task.Delay(1s, timeProvider)
// synchronously during Advance, so the poll loop exits instantly.
time.Advance(TimeSpan.FromSeconds(10));

// Pump the async state machine: at most 5 real-time ticks.
for (var i = 0; i < 5 && !task.IsCompleted; i++)
    await Task.Delay(1);

await task;
```

### Steps

1. Open `tests/VisualRelay.Tests/BackendLifecycleStatusTests.cs`.
2. In `Start_RemovesLegacyRepoLocalState` (line ~128–134): replace the
   `while`/`Advance`/`Yield` pump with the two-phase pattern above.
3. In `Start_MissingToolchain_LogsRemediation_Exit1` (line ~159–163): same
   replacement.
4. Run the full suite to green. Expected: same assertions pass, each test
   drops from 10–33 s to well under 50 ms.

### Guardrails
- The `ReadyTimeout` is set to 50 ms in both tests — advancing 10 s is more
  than enough.
- `ManualTimeProvider.Advance` fires `Task.Delay(timeout, timeProvider)` timers
  synchronously; `PollReadinessAsync` calls `Task.Delay(1s, timeProvider)`.
- Coverage rules: no assertion changes, no deletions, no weakenings.

## Expected savings

Per test: ~33 s → 0.05 s, ~11 s → 0.05 s (99.9 % reduction each). Wall-time
saving: ~44 s tail removed.

## Commit-message evidence

Measure before and after while implementing, then put one filled-in evidence
bullet in the commit message body, following the attached
`commit-message-evidence.md`. Never pre-fill that bullet — numbers are measured
at implementation time and go into the eventual commit message, nowhere else.
