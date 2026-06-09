# Continue draining past a flagged/stalled task instead of halting on the first flag

`RelayQueueController.DrainAsync` (`src/VisualRelay.Core/Queue/RelayQueueController.cs`)
is the "run all tasks" / drain loop over the pending queue. Today it **halts the
entire drain on the first flagged task**:

```csharp
var outcome = await _runner.RunTaskAsync(RootPath, task.Id, cancellationToken);
...
if (circuitBreaker.ShouldHalt(RootPath, outcome))   // true on the FIRST flag
{
    State = ... ReviewNeeded/Failed;
    return results;                                  // remaining tasks never run
}
```

`DrainCircuitBreaker.ShouldHalt` (`src/VisualRelay.Core/Queue/DrainCircuitBreaker.cs`)
returns `true` immediately for **any** `Flagged` outcome that is not a
`"commit rejected:"` reason (the `else if (outcome.Status == Flagged)` branch
returns true on the first occurrence). Only `"commit rejected:"` flags are counted
and tolerated (up to `CommitRejectThreshold = 2` consecutive).

A task that **stalls** is killed by the subagent timeout and surfaces as `Flagged`
(reason: "swival … timed out …"), so it is caught by that same branch and halts
the drain immediately. Net effect: in unattended "run all tasks", a single
problematic task (a flag, or a stall) blocks every remaining pending task. (This
is why an external skip-and-continue wrapper is currently needed to drive the
queue to completion.)

## Goal

Make the drain resilient to individual flagged/stalled tasks:

- An **isolated flag** (including a stalled-out task) should **set that task aside
  for review and continue** to the next pending task — not halt the whole drain.
- The drain should still **halt when failure is systemic** — i.e. a configurable
  number of **consecutive** flags with no successful commit in between — so a
  fundamentally broken run still stops instead of burning the whole queue (and its
  token cost). A `Committed` outcome resets the consecutive counter.

This generalizes the existing consecutive-commit-reject circuit breaker to all
flag types.

## Approach (suggested)

- In `DrainCircuitBreaker`, track consecutive **flags** (any reason), not just
  commit-rejects. `ShouldHalt` returns `true` only when the consecutive-flag count
  reaches a threshold (a named constant / tunable, e.g. `ConsecutiveFlagThreshold = 3`).
  Reset the counter to 0 on any non-`Flagged` (i.e. `Committed`) outcome. Keep the
  `"commit rejected:"` path on its existing (lower) threshold if you want commit-gate
  breakage to trip faster — but a normal flag must NOT return true on its first
  occurrence. Preserve the `DRAIN-HALTED` marker semantics for the actual halt.
- In `RelayQueueController.DrainAsync`, when `ShouldHalt` returns `false` for a
  flagged outcome, **continue the loop** (it already does, since it only returns on
  halt) but mark the just-flagged task `NeedsReview` so it is set aside and not
  re-queued on a later `RefreshAsync` (the queue is built from
  `Tasks.Where(t => !t.NeedsReview)`). Keep recording the outcome in `results` and
  writing the per-task review marker.
- On the halting case, return with the existing State mapping
  (`ReviewNeeded` / `Failed`). Consider a distinct terminal State or a summary
  (e.g. "completed with N flagged for review") so the GUI can show that the drain
  finished but some tasks need attention vs. it halted early.
- `MainWindowViewModel.Execution.cs` drives `DrainAsync`; update it only if needed
  so the run-all UI reflects "continued past N flagged" vs "halted".

## Files

- `src/VisualRelay.Core/Queue/DrainCircuitBreaker.cs` (consecutive-flag counting + threshold)
- `src/VisualRelay.Core/Queue/RelayQueueController.cs` (`DrainAsync`: mark flagged task `NeedsReview`, continue)
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs` (only if the GUI needs to reflect the new outcome)

## Tests

Use the existing queue test doubles / a fake `IRelayTaskRunner` returning scripted outcomes.

- 3 pending tasks, the middle one `Flagged` → **all 3 run**; the flagged one is set
  aside (`NeedsReview`); the drain does NOT stop after the flag.
- A **stalled** task (Flagged with a timeout reason) is treated like any other flag
  — skip + continue, not an immediate halt.
- `ConsecutiveFlagThreshold` consecutive flags → drain halts at the threshold, writes
  the `DRAIN-HALTED` marker, leaves the rest un-run.
- A `Committed` task between flags **resets** the counter (flag, commit, flag, commit…
  never reaches the threshold → the queue drains fully).
- (If kept) `"commit rejected:"` flags retain their existing lower consecutive threshold.

## Notes

This makes the built-in "run all tasks" behave like a robust unattended drain — exactly
what an external skip-and-continue loop provides — while keeping a circuit breaker so a
systemic failure still stops the run. Keep `RelayQueueController.cs` / `DrainCircuitBreaker.cs`
within the line guard.
