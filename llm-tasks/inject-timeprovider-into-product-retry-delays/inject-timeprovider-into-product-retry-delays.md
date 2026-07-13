## Task: Inject TimeProvider into product-side retry delays

Replace every `Task.Delay(delay, cancellationToken)` in the production source code that lacks a `TimeProvider` argument with `Task.Delay(delay, timeProvider ?? TimeProvider.System, cancellationToken)` and thread the `TimeProvider?` parameter through to the call site. This lets test suites drive those delays with `ManualTimeProvider` instead of real wall-clock seconds.

### Baseline measurements

From the parent task's `timings-baseline.txt`, the full suite runs at ~119 s wall time. The product-side delays below are real-time `Task.Delay` calls that tests exercise but cannot currently accelerate:

| Location | Delay | Purpose | Real cost per hit |
|---|---|---|---|
| `src/VisualRelay.Core/Execution/BackendLifecycle.Start.cs:172` | 1 000 ms | startup readiness polling | 1 s per startup iteration |
| `src/VisualRelay.Core/Execution/BackendLifecycle.cs:147` | 200 ms | stop‑sequence yield | 200 ms per stop |
| `src/VisualRelay.Core/Execution/PlanningWorktree.cs:224` | 250 ms‑1 s | git‑op retry backoff | 250 ms – 1 s per retry |
| `src/VisualRelay.Core/Execution/PlanningWorktree.cs:232` | 250 ms‑1 s | git‑op retry backoff | 250 ms – 1 s per retry |
| `src/VisualRelay.Core/Execution/GitCommitter.cs:250` | 250 ms‑1 s | git‑op retry backoff | 250 ms – 1 s per retry |

None of these accept a `TimeProvider` — they burn real seconds under any test that enters a retry or polling path.

### Prescribed approach

1. Follow the established pattern in the codebase (see `ProcessRunners.cs:64`, `SandboxedTestRunner.Watched.cs:88`, `ProcessCapture.cs:73`, `ProcessRunners.Watchdog.cs:78`): add a `TimeProvider? timeProvider = null` parameter to each method, sinking to `TimeProvider.System` at the call site.

2. Replace:
   ```csharp
   await Task.Delay(delay, cancellationToken);
   ```
   with:
   ```csharp
   await Task.Delay(delay, tp, cancellationToken);
   ```
   where `tp = timeProvider ?? TimeProvider.System`.

3. Thread the parameter through intermediate methods so the test call site can inject a `ManualTimeProvider`.

4. In tests that exercise a retry or poll path, inject a `ManualTimeProvider` and use the Advance-loop pattern established in `BackendReadinessProbeTests.cs`:
   ```csharp
   var time = new ManualTimeProvider();
   var task = MethodUnderTest(..., timeProvider: time);
   while (!task.IsCompleted)
   {
       time.Advance(TimeSpan.FromMilliseconds(delayMs));
       await Task.Yield();
   }
   var result = await task;
   ```

5. Keep a small set of integration tests (tagged with `SlowIntegration.SkipIfNotOptedIn()`) that exercise the real-time path with `TimeProvider.System` to retain confidence that wall-clock delays work in production.

### Expected saving

- **BackendLifecycle tests**: ~1 s saved per startup‑retry scenario, ~200 ms per stop.
- **GitCommitter/PlanningWorktree tests**: ~250 ms – 1 s per retry scenario.
- Total: ~2–3 s of full‑suite wall time eliminated. More importantly, this removes an entire class of hidden wall-clock waste from tests that trigger error-recovery paths.

### Pitfalls

- Do NOT delete the delay itself — the backoff is intentional (prevents busy‑retry hammering).
- Every `Task.Delay` that gets a TimeProvider must keep its `CancellationToken`.
- The existing `RealSleepGuardTests` AST scan flags `Task.Delay` calls lacking a TimeProvider argument; re-run the guard after this change to confirm no bare delays remain in the source tree.

### Coverage rules

- Never delete, disable, skip, or weaken a test. Speed comes from doing the same verification cheaper, not from verifying less.
- Any test that genuinely requires a real-time boundary should use `SlowIntegration.SkipIfNotOptedIn()` and run only with `VR_RUN_SLOW_INTEGRATION=1`. The default suite covers the same logic with virtual time.

### Commit‑message evidence

```
- test time dropped from 119s to 117s, saving 2s (full-suite wall time)
```
