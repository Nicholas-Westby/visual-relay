# Zero Real-Time Waits in the Suite: Virtualize Every Clock, Gate Every OS-Semantics Test

Part of the test-suite speed push (hard ceiling: full suite under 60 s; 45 s is the
aspirational target). Rule being enforced
here, set by the maintainer: **no always-on test may wait on the real wall clock. Zero
exceptions.** Every timing behavior is either driven through a virtual clock
(`ManualTimeProvider`, the established house pattern — see the `ActivityWatchdog`
virtualization and `ActivityWatchdogDecisionTests`), or it is a genuine OS-semantics
test (real kill/reap/signal/pipe behavior) and runs only behind the slow-integration
opt-in. Tests that "allow a little real waiting" have been tolerated until now; that
allowance is revoked.

## Why (measured 2026-07-08, host Mac)

- The process-watchdog families — `SwivalSubagentRunnerWatchdogTests`,
  `SwivalSubagentRunnerEscalationTests`, `SwivalSubagentRunnerContractRetryTests`,
  `SandboxedTestRunnerReapTests`, `SandboxedTestRunnerTests`,
  `ProcessCaptureGracefulStopTests`, `StageInputArtifactIntegrationTests`,
  `FdLeakTests` — measured ~97 facts summing ~96 s at only 56 % CPU: they spawn real
  bash/perl children and sit in real first-output/inactivity/kill-escalation windows.
  Worst: `GracefulStop_ChildIgnoresInt_ForceKilled` 10.6 s,
  `RunAsync_SilentCpuBurn_SurvivesInactivityWindow` 9.3 s,
  `RunAsync_PersistentStall_AlsoEscalates_ThreeRunsThenFails` 6.1 s.
- These same facts are the suite's flake source, because they assert wall-clock races
  that lose under worker-pool load: on 2026-07-08 alone,
  `SandboxedTestRunnerReapTests.RunWatchedAsync_WrapperOutlivesFinishedTests_ReturnsRealResultPromptly`
  failed a full-suite run (idle-reap expected ~1.5 s, took 4.2 s), and an earlier
  fix-verify measured `RunAsync_PersistentStall_AlsoEscalates_ThreeRunsThenFails` at
  ~1-in-4 full-suite failure rate. Deterministic causality fixes both cost and flakes.

## Complete inventory of real waits in `tests/VisualRelay.Tests/` (verified by grep)

Process-window facts: the families above (real 1.5–2 s grace windows, 7 s hard caps,
200 ms cpu-sample intervals, kill escalation ladders, perl/bash `tail -f /dev/null`
children). Small raw delays: `FdLeakTests` (2× `Task.Delay(100)`),
`BackendLifecycleStatusTests`/`ObsidianDrainSummaryTests`/`BackendConfigStepTests`
(`Task.Delay(50)`), `MainWindowViewModelFixTaskTests.Execution` (`Task.Delay(10)`),
`RelayQueueControllerParallelTests` (`Task.Delay(100)`, `Task.Delay(200, ct)`),
`PlanPhaseTestDoubles` (`Task.Delay(50, ct)`), `ControlServerTests`
(`Thread.Sleep(retryMs)`), `SettingsTestHelpers` (`Thread.Sleep(10)`),
`TestFileSystem` (`Thread.Sleep(attempt * 25)`). Production test seam:
`MainWindowViewModel.WaitForRewriteToFinishForTests` polls with `Task.Delay(10)`
(`src/VisualRelay.App/ViewModels/MainWindowViewModel.Rewrite.cs`).
`ActivityWatchdogDecisionTests.WaitAsync` already uses TimeProvider-parameterized
delays — that pattern is the target state, not a violation.

## What to build

1. **TimeProvider seams in the supervision stack (additive, default = system time,
   zero behavior change).** `SandboxedTestRunner.RunWatchedAsync`, the
   `SwivalSubagentRunner` watchdog/escalation path (`ProcessRunners.RunAsync.cs`),
   `ProcessCapture`'s cpu-sample loop (`Task.Delay(intervalMs, ct)` in
   `ProcessCapture.cs`) and graceful-stop step delay (`Task.Delay(200)` in
   `ProcessCapture.GracefulStop.cs`) each accept a `TimeProvider` (defaulted to
   `TimeProvider.System`), used for every deadline, sample interval, and delay.
   `RelayTraceTailer`'s 200 ms poll (`Traces/RelayTraceTailer.cs`) likewise.
2. **Rewrite the watchdog-LOGIC facts onto the virtual clock with scripted activity.**
   Stall detection, first-output vs inactivity deadlines, escalation ladder
   (tier/turns progression, `maxStallRetries`), retry-then-recover, killed-output
   persistence and formatting, hard-cap saturation: drive them through the existing
   decision/pulse seams (`ActivityWatchdog` pulses, as `ActivityWatchdogDecisionTests`
   does) plus the new TimeProvider seams, with `ManualTimeProvider.Advance(...)`
   replacing every real window. No spawned children in any always-on fact of these
   families; each lands in the low-milliseconds range.
3. **Gate the genuine OS-semantics facts.** Facts whose subject IS the operating
   system — SIGINT trap/ignore kill escalation, setpgid group reap, orphan children
   inheriting pipe write-ends, prompt return on parent exit — keep their real
   processes and real windows but move behind
   `SlowIntegration.SkipIfNotOptedIn()` (env `VR_RUN_SLOW_INTEGRATION=1`; mirror
   `NonoIntegration.SkipIfNotOptedIn()` in `tests/VisualRelay.Tests/NonoIntegration.cs`,
   keeping the method name the `RealBuildSubprocessGuardTests` AST scan recognizes; if
   the helper already exists in-tree, reuse it). Each gated fact gets an always-on
   virtual-clock sibling asserting the same decision logic. Run the gated set
   opted-in once during this task and record the result.
4. **Replace every small raw delay** in the files listed above with causality: await
   the actual operation task, a `TaskCompletionSource` gate the code under test
   completes, or a `ManualTimeProvider` advance — per the house "await, don't poll"
   doctrine already encoded in `BannedSymbols.txt` (`WaitHelpers.WaitUntilAsync`
   entries). `WaitForRewriteToFinishForTests`: drop the poll loop — pump
   `Dispatcher.UIThread.RunJobs()`, await the captured rewrite task
   (`_rewriteTasksForTests`), pump once more; no `Task.Delay`.
   `TestFileSystem`/`SettingsTestHelpers` retry sleeps: make the retry pacing
   injectable and instant under test.
5. **Enforce it forever.** Extend `RealSleepGuard`
   (`tools/VisualRelay.Guards`, matcher exercised by
   `tests/VisualRelay.Tests/RealSleepGuardTests.cs`): within
   `tests/VisualRelay.Tests/**`, flag EVERY `Thread.Sleep(...)` and every
   `Task.Delay(...)` call that lacks a `TimeProvider` argument — any duration,
   cancellable or not (the current matcher only flags long uncancellable ones; that
   loophole is what this task closes). Allowlist, by the guard's existing
   filename-exemption mechanism: the gated real-integration files and the guard's own
   fixture files. Add matcher facts in the `RealSleepGuardTests` style, and prove the
   live gate bites with a temporary offender (then remove it; note the check in the
   summary).

## Done when

- `grep -rn "Task\.Delay\|Thread\.Sleep" tests/VisualRelay.Tests --include="*.cs"`
  shows only TimeProvider-parameterized calls, allowlisted gated files, and guard
  fixtures.
- The families listed under "Why" have always-on variants summing **under 3 s** in a
  family filter run, with every previous assertion represented either in a virtual
  fact or a gated real fact.
- Previously flaky facts' replacements pass a 10× isolated loop
  (`for i in $(seq 10); do dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build --filter "<the replaced facts>" || break; done`).
- Extended `RealSleepGuard` is green live, has matcher coverage, and demonstrably
  fails on an out-of-allowlist offender.
- Opted-in gated set passes on the host. Full suite green 3×; `./visual-relay check`
  passes.

## Guardrails

- Production seam additions are TimeProvider parameters (plus whatever minimal
  constructor threading they need) with system defaults — no reordering, no changed
  deadlines, no altered kill behavior. Nothing else in production moves.
- Never "fix" a timing fact by widening its window — the window must become virtual or
  the fact becomes gated. No new always-on fact may contain a wall-clock bound.
- OS-semantics coverage is gated, never deleted. The `Watchdog` xUnit collection stays
  for the gated facts.
- Only the `SlowIntegration.SkipIfNotOptedIn()` idiom may skip; no string skips.
- Conventional Commits; files under the size guard.
