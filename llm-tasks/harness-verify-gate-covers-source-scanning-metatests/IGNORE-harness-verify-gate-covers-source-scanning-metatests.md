# Harness: investigate whether per-task verify gates source-scanning meta-tests

> **IGNORE (deferred, not abandoned).** Observation from the 2026-06-28 Run All. Investigate
> before fixing — the mechanism is not yet confirmed.

## Observed (facts)
After Run All committed all 10 tasks (HEAD `3aa82cc`), the full suite was RED on two
**source-scanning meta-tests** — yet every task had passed its per-task verify and committed:
1. `SplitGuardVerificationTests` fact-count baseline drifted (154 vs 159) as tasks added
   test methods without bumping the manual baseline.
2. `RealSleepGuardTests.AllTestProjectCsFiles_AreSleepFree` flagged a real `sleep 0.5`
   fixture added by the watchdog task (`SwivalSubagentRunnerWatchdogTests.ActivityWatchdog.cs`)
   with no `// vr-allow-sleep` suppression.
(Both were fixed afterward in commit `0579975`; HEAD is green now.)

## To investigate
Why did the per-task verify (full suite under nono in a worktree) NOT fail on these? Likely
candidates to check: do `RealSleepGuard`/`SplitGuardVerification`/`SourceEnumeration`-style
meta-tests resolve the repo root to the **verify worktree** or to the **main checkout**? If
they scan a path that isn't the worktree, a task's source-hygiene regression won't be gated.

## What to do (after confirming)
- Ensure source-scanning meta-tests respect the verify worktree so test-hygiene regressions
  are gated like everything else; OR document why they intentionally aren't.
- Make the `SplitGuardVerification` fact-count baseline non-brittle (auto-derive or relax) so
  routine test additions don't silently red the suite / require manual bumps.
- General-purpose: no VR-specific assumptions in product code.
