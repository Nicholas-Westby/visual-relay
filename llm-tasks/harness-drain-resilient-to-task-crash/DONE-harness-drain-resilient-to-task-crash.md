# Harness: drain continues past a per-task crash instead of aborting the queue

## Current state (researched)

### The crash scenario

A 3-task batch drain crashed with `System.IO.IOException: Too many open files` at
`RelayDriver.FlagAsync → Directory.CreateDirectory`. The exception propagated out of
`RunTaskAsync` to `DrainAsync`, which has no per-task exception guard, and from there
to `Main`, aborting the entire queue. The two remaining tasks never ran.

### Where unhandled exceptions escape today

`src/VisualRelay.Core/Queue/RelayQueueController.cs` lines 203–246 (Phase 2 serial
execute loop):

```csharp
// Line 217
var outcome = await _runner.RunTaskAsync(RootPath, task.Id, cancellationToken);
results.Add(outcome);
// ... circuit-breaker and marker logic ...
```

`_runner.RunTaskAsync` is `RelayDriver.RunTaskAsync`
(`src/VisualRelay.Core/Execution/RelayDriver.cs:23–295`). Its outer `try/catch`
(`RelayDriver.cs:291–294`) catches exceptions and converts them to a flagged
`RelayTaskOutcome` via `FlagAsync`:

```csharp
catch (Exception ex)
{
    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 0,
        $"exception: {ex.Message}", ex.ToString(), statusEntries, cancellationToken);
}
```

**However**, `FlagAsync` itself calls `Directory.CreateDirectory` to ensure the task
directory exists before writing the NEEDS-REVIEW marker. When the OS is out of file
descriptors (or any other I/O failure), `Directory.CreateDirectory` throws a second
exception — inside the `catch` handler. This secondary exception propagates out of
`RunTaskAsync` entirely, bypassing the outcome-recording path.

The call chain:

```
DrainAsync
  → RunTaskAsync       ← outer try/catch (line 291)
      → FlagAsync      ← called from catch
          → Directory.CreateDirectory   ← throws (e.g. EMFILE)
                                          propagates OUT of RunTaskAsync
  ← unhandled exception reaches DrainAsync line 217
      ← no try/catch in DrainAsync around RunTaskAsync
  ← propagates to caller (Main / GUI)
  ← entire queue aborted
```

### The existing circuit breaker (backstop, already in place)

`src/VisualRelay.Core/Queue/DrainCircuitBreaker.cs` — `ShouldHalt` counts consecutive
flags and halts after `ConsecutiveFlagThreshold` (default 3) consecutive flagged
outcomes with no committed task in between. This is the correct systemic-failure
backstop. The fix in this task must leave the circuit breaker intact and let it be the
only halt mechanism.

Note: `DONE-drain-continue-past-flagged-tasks.md` already generalizes consecutive-flag
counting for all flag types. The remaining gap is specifically that an **unhandled
exception** from `RunTaskAsync` bypasses both the circuit breaker and per-task outcome
recording entirely.

## What to build

### 1. Guard `RunTaskAsync` in `DrainAsync`

In `src/VisualRelay.Core/Queue/RelayQueueController.cs`, wrap the `RunTaskAsync` call
(line 217) in a `try/catch` that converts any unhandled exception into a synthetic
flagged `RelayTaskOutcome` and continues the loop:

```csharp
RelayTaskOutcome outcome;
try
{
    outcome = await _runner.RunTaskAsync(RootPath, task.Id, cancellationToken);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    // Drain was cancelled externally — stop immediately, don't flag.
    State = RelayQueueState.Failed;
    return results;
}
catch (Exception ex)
{
    // Unexpected exception from RunTaskAsync (e.g. IOException from FlagAsync).
    // Treat as a flagged outcome so the drain continues to the next task.
    outcome = new RelayTaskOutcome(task.Id, RelayTaskOutcomeStatus.Flagged,
        null, $"unhandled exception: {ex.GetType().Name}: {ex.Message}", null);
}
```

After this block, the existing circuit-breaker check (`circuitBreaker.ShouldHalt`) runs
as normal on the synthetic flagged outcome. Three consecutive unhandled-exception flags
will still halt the drain.

**Write the NEEDS-REVIEW marker best-effort**: after catching the unhandled exception,
attempt `WriteNeedsReviewMarker` in a second try/catch. If that also fails, log a
DrainSummaryLog entry with the original exception text so the task is not silently
lost. The task remains visible in the queue's `Tasks` collection as un-removed, so a
subsequent `RefreshAsync` will see it as still pending.

### 2. Harden `FlagAsync` against secondary exceptions

In `RelayDriver.FlagAsync` (partial method in `RelayDriver.Snapshot.cs`), wrap the
`Directory.CreateDirectory` call and subsequent file writes in a try/catch so that if
they fail, `FlagAsync` still returns a valid flagged `RelayTaskOutcome` carrying the
**original** flag reason (the root cause), not the secondary I/O exception. This is
defence-in-depth: even without the DrainAsync guard, `RunTaskAsync`'s outer catch would
then successfully return a Flagged outcome.

## Done when

- **Write failing tests first** before any source changes:

  - `DrainAsync_UnhandledExceptionFromRunTask_ContinuesToNextTask`: construct a
    `RelayQueueController` with a fake `IRelayTaskRunner` whose first call throws
    `InvalidOperationException` (simulating `FlagAsync` failure). Assert the drain
    runs the second task, returns two outcomes, and the exception-task outcome is
    `Flagged`. The second task must not be skipped.

  - `DrainAsync_ConsecutiveExceptions_HaltsAfterThreshold`: fake runner throws on all
    tasks. Assert drain halts after 3 consecutive exceptions (circuit breaker threshold)
    and does not run tasks beyond the threshold.

  - `FlagAsync_DirectoryCreateFails_ReturnsFlag`: use a `RelayDriver` with an injected
    filesystem stub or a temp directory set to read-only. Simulate `FlagAsync` being
    called when `Directory.CreateDirectory` throws. Assert `RunTaskAsync` returns a
    valid `Flagged` outcome rather than throwing.

- **`DrainAsync` exception guard** is in place at
  `RelayQueueController.cs` around `_runner.RunTaskAsync`.
- **`FlagAsync`** does not re-throw secondary exceptions from directory/file ops —
  it returns a `Flagged` outcome carrying the original flag reason.
- **Circuit breaker is preserved**: 3 consecutive unhandled-exception flags still halt
  the drain. A single exception-flag followed by a committed task resets the counter.
- **`OperationCanceledException` pass-through**: a cancellation request (user pause/stop)
  is not swallowed as a flag — it propagates cleanly.
- **`./visual-relay check` is green** — all pre-existing tests pass unmodified.
- **Files stay under 300 lines each.** Changes to `RelayQueueController.cs` are a small
  try/catch block; `RelayDriver.Snapshot.cs` changes are a defensive wrap around the
  directory/file ops in `FlagAsync`.
- **Conventional Commit** subject candidates:
  - `fix(drain): catch RunTaskAsync exceptions to continue drain instead of aborting`
  - `fix(driver): defend FlagAsync against secondary IOException on EMFILE`

## Overlap check

`DONE-drain-continue-past-flagged-tasks.md` — already done; generalized the circuit
breaker to count all consecutive flags. This task closes the remaining gap: an unhandled
**exception** (not just a flagged outcome) from `RunTaskAsync` that bypasses the circuit
breaker entirely. These are complementary: the DONE task makes flagged outcomes non-fatal
one-at-a-time; this task ensures that even exception-level failures (e.g. EMFILE during
`FlagAsync`) are converted to outcomes before reaching the circuit breaker.
