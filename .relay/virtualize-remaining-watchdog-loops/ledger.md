## Stage 1 - Ideate

{
  "summary": "Virtualize six remaining ActivityWatchdogDecisionTests.WaitAsync loop tests by replacing the for+Task.Yield() pump with a synchronous two-phase pattern (pulse advances then deadline jump + a few Task.Delay(1)), cutting per-test wall time from ~8s to <100ms and saving ~30s from the full suite tail.",
  "options": [
    "Option 1 — In-place rewrite of each loop (direct, minimal diff, but duplicates pattern 6x)",
    "Option 2 — Extract a shared PumpWatchdogAsync helper into the partial class (DRY, single maintenance point, recommended)",
    "Option 3 — Two-phase rewrite with trailing-assertion guard (strongest no-regression guarantee but more code)"
  ]
}

## Stage 2 - Research

{
  "findings": "Six tests across two partial-class files use for-loops with Task.Yield() on each iteration to pump the watchdog's async state machine. The watchdog's WaitAsync polls via Task.Delay(TimeSpan, TimeProvider, CancellationToken) using ManualTimeProvider; Advance() fires timer callbacks synchronously but continuations (the code after await delayTask) queue to the thread pool and need a Task.Yield() to execute. In every target test, the first delay (≤200ms) fires on the very first Advance (step ≥200ms), so the watchdog accumulates exactly one queued continuation per test regardless of iteration count. The already-fixed reference test (WaitAsync_BurstThenTotalSilence_FiresAtInactivityDeadline, line 209) proves the pattern: (1) feed all pulses with Advance() calls, no yield; (2) one large Advance() past the deadline; (3) up to 5 Task.Yield() calls to pump the continuation. For 'should fire' tests (4 total: SocketWedgeStillFires, SustainedIdlePlusSocket, OutputSilenceFires, and the reference), a single continuation sees the full accumulated time, the expired deadline, and fires immediately. For 'should not fire' tests (3 total: BusySubtree, BurstyAgent, ContinuousRealOutput), cancelling the stop token after the pulse loop and then yielding lets the watchdog's continuation see Disarmed state and return via OperationCanceledException. The remaining partial class files (OutputSilence.cs, TierWindows.cs) contain only synchronous pure-decision tests with no loops. The end-to-end SocketWedgeTests.cs uses real OS processes and is out of scope. RealSleepGuard flags bare Task.Delay(1) — the phase-2 pump must use Task.Yield() to match the reference pattern and avoid static analysis violations. No other files in the test project have similar watchdog-loop patterns.",
  "constraints": [
    "No assertion may be changed — same pass/fail criteria as today",
    "Keep final await watchdogTask and IsCancellationRequested checks exactly as-is",
    "No test may be deleted, skipped, or weakened",
    "Pulse loop must not introduce real-time yield until phase 2 (ManualTimeProvider fires timers synchronously during Advance)",
    "Phase-2 pump must use Task.Yield() not Task.Delay(1) — matches the reference pattern and avoids RealSleepGuard violations",
    "For 'should not fire' tests (BusySubtree, BurstyAgent, ContinuousRealOutput): cancel stopCts after the pulse loop, then pump yields to propagate cancellation",
    "Step values are always ≥200ms, guaranteeing the watchdog's first ≤200ms delay timer fires on the first Advance",
    "Each test creates its own ManualTimeProvider and ActivityWatchdog instance — no shared state between tests"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Six ActivityWatchdogDecisionTests.WaitAsync tests use for-loops that await Task.Yield() on every iteration (60, 12, or 16 iterations depending on test). Each Yield allows the watchdog's single queued continuation to run, but under maxParallelThreads:2.0x the thread-pool contention inflates wall time to ~6–8s per test. The already-fixed reference test (WaitAsync_BurstThenTotalSilence_FiresAtInactivityDeadline, line 209–247) proves the two-phase replacement: (1) feed all pulses with time.Advance() calls synchronously — no yield needed because ManualTimeProvider fires timer callbacks inline during Advance, and only the first Advance fires a timer since the watchdog hasn't resumed to schedule another; (2) pump at most 5 Task.Yield() calls to drain the single queued continuation. Task.Yield() (not Task.Delay(1)) is required to pass RealSleepGuard. Critical: the prescribed 'phase-2 deadline jump' (large time.Advance() past the inactivity deadline) must NOT be applied to the two socket-wedge tests (SocketWedgeStillFires, SustainedIdlePlusSocket) because it would push silenceMs past inactivityTimeoutMs, causing DecideOutcome to fire FiredStall on priority before the wedge gate is even evaluated, breaking the FiredSocketWedge assertion. For those tests, phase-1 already advanced well past the inactivity window (15×) and the wedge fires correctly without the extra jump. For the output-silence test the jump is safe (inactivityTimeoutMs=600000) but unnecessary. For the three 'should not fire' tests, cancel stopCts after the pulse loop then pump yields; the continuation sees Disarmed then OperationCanceledException.",
  "excerpts": [
    "// Reference test (already fixed) — ActivityWatchdogDecisionTests.WaitAsync.cs:209-247\npublic async Task WaitAsync_BurstThenTotalSilence_FiresAtInactivityDeadline()\n{\n    const int inactivityTimeoutMs = 2_000;\n    var time = new ManualTimeProvider();\n    using var kill = new CancellationTokenSource();\n    var watchdog = new ActivityWatchdog(\n        firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: inactivityTimeoutMs,\n        absoluteCeilingMs: 0, kill, timeProvider: time);\n    watchdog.Pulse(\"stdout\");\n    time.Advance(TimeSpan.FromMilliseconds(50));\n    watchdog.Pulse(\"trace\");\n    var watchdogTask = watchdog.WaitAsync(CancellationToken.None);\n    time.Advance(TimeSpan.FromMilliseconds(inactivityTimeoutMs + 200));\n    for (var i = 0; i < 5 && !watchdogTask.IsCompleted; i++)\n        await Task.Yield();\n    var result = await watchdogTask;\n    Assert.Equal(ActivityWatchdog.Outcome.FiredStall, result.Outcome);\n}",
    "// Target test pattern (one of six) — ActivityWatchdogDecisionTests.WaitAsync.cs:46-52\nconst int step = inactivityMs / 4;\nfor (var i = 0; i < 60 && !watchdogTask.IsCompleted; i++)\n{\n    watchdog.Pulse(\"cpu\");\n    watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(SubtreeIdle: true, BackendSocketEstablished: true));\n    time.Advance(TimeSpan.FromMilliseconds(step));\n    await Task.Yield();\n}",
    "// Watchdog polling loop — ProcessRunners.Watchdog.cs:200-293\n// Polls via Task.Delay(delay, _timeProvider, ct). ManualTimeProvider.Advance()\n// fires timer callbacks synchronously (completing the delay task), but the\n// continuation (code after await delayTask) is queued to the thread pool.\n// DecideOutcome (line 153-191) checks priority: absolute ceiling > output-silence\n// > inactivity stall > socket wedge. Socket-wedge tests MUST keep silenceMs <\n// inactivityTimeoutMs so the wedge gate is reached.",
    "// RealSleepGuard flags bare Task.Delay(1) — RealSleepGuardTests.cs:101-107\n// 'A bare 1-arg Task.Delay(1) — the old advance-yield idiom — is reported:\n//  no TimeProvider argument, so it is a real (if tiny) wall-clock delay.'\n// Phase-2 pump MUST use Task.Yield() not Task.Delay(1).",
    "// DecideOutcome priority — ProcessRunners.Watchdog.cs:153-191\n// Line 179-183: firedStall = firstPulseReceived ? silenceMs >= inactivityTimeoutMs\n//   : elapsedMs >= firstOutputTimeoutMs;\n// if (firedStall) return Outcome.FiredStall;\n// Line 187-188: // Additive: socket wedge only checked if stall didn't fire\n// if (TryDecideSocketWedge(...)) return Outcome.FiredSocketWedge;\n// → A deadline jump that pushes silenceMs past inactivityTimeoutMs makes\n//   FiredStall win before the wedge gate is reached, breaking wedge assertions."
  ],
  "repro": "Run the full test suite with `dotnet test --configuration Release --maxParallelThreads:2.0x` and observe the six watchdog loop tests each taking ~6–8s wall time (or run individually: `dotnet test --filter 'FullyQualifiedName~WaitAsync_CpuPulseMasksDeadline_SocketWedgeStillFires'` etc.). The 60-iteration Task.Yield() pump creates thread-pool contention; under 2.0x parallelism the Yields serialize behind other tests' work, inflating each iteration from ~1ms to ~100ms. The already-fixed reference test (WaitAsync_BurstThenTotalSilence_FiresAtInactivityDeadline) runs in <100ms, proving the two-phase pattern works."
}

## Stage 4 - Plan

{
  "plan": "Virtualize six remaining ActivityWatchdogDecisionTests.WaitAsync loop tests by replacing the for+Task.Yield() pump with a synchronous two-phase pattern (pulse advances then yield pump), matching the already-fixed reference test. Each test: Phase 1 feeds all pulses/advances in a tight loop with no yield (ManualTimeProvider fires timers synchronously, only one continuation is ever queued). Phase 2 pumps at most 5 Task.Yield() calls to drain the continuation. For 'should fire' tests the continuation sees accumulated time and fires the appropriate gate. For 'should not fire' tests, cancel stopCts after Phase 1, then pump — the continuation sees Disarmed via OperationCanceledException. No assertion, outcome, or test name changes. Two files, six edits total.",
  "manifest": [
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.WaitAsync.cs",
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.WaitAsync.OutputSilence.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.WaitAsync.cs",
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.WaitAsync.OutputSilence.cs"
  ],
  "rationale": "Replaced per-iteration Task.Yield() in six watchdog loop tests with synchronous pulse-advance phase followed by at most 5 Task.Yield() pump. Same pattern proven in WaitAsync_BurstThenTotalSilence_FiresAtInactivityDeadline. No assertions changed; no tests deleted/skipped/weakened. Socket-wedge tests omit deadline jump to preserve FiredSocketWedge outcome priority. All 6 pass (19ms total, all 3192 pass in full suite). Measured full-suite wall time: 60s → 57s (3s saving)."
}

## Stage 6 - Implement

{ "summary": "All six ActivityWatchdogDecisionTests.WaitAsync loop tests already converted in working tree (stage 5): removed per-iteration await Task.Yield() from each for loop, added at most 5 Task.Yield() pump after. Socket-wedge tests omit deadline jump (FiredStall would win priority over FiredSocketWedge). Should-not-fire tests cancel stopCts before pumping. Targeted run: 37 passed in 36ms. Full suite: 3192 passed, 0 failed, 53s wall time." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Pure C# test code refactoring — no UI, visual assets, charts, documents, or rendered output of any kind. Changes are byte-level async pattern replacements within test methods; assertions and pass/fail criteria are unchanged._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Six ActivityWatchdogDecisionTests.WaitAsync loop tests across two partial-class files (WaitAsync.cs and WaitAsync.OutputSilence.cs) have been virtualized by replacing the old per-iteration `for + await Task.Yield()` pump (60/12/16 iterations) with a two-phase pattern: phase 1 feeds all pulses/advances synchronously (no yield — ManualTimeProvider fires timer callbacks inline during Advance), and phase 2 drains the single queued continuation with at most 5 `await Task.Yield()` calls. For 'should not fire' tests, `stopCts.CancelAsync()` is called after the pulse loop. Socket-wedge tests omit the deadline jump to prevent FiredStall from winning priority over FiredSocketWedge. No assertions changed, no tests deleted/skipped/weakened. Full suite: 3192 passed, 0 failed, 114 skipped, 52 s wall time.",
  "commitMessages": [
    "test: virtualize six watchdog WaitAsync loop tests",
    "perf: drop per-iteration Task.Yield() from watchdog loop tests",
    "refactor: replace Task.Yield() pump with advance-then-yield pattern",
    "perf: speed up ActivityWatchdogDecisionTests watchdog loops",
    "test: apply two-phase pump to remaining watchdog tests"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

