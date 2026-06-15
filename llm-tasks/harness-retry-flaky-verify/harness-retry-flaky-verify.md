# Harness: retry a transient verify/test failure once before entering fix-verify

When a target repo's test command fails at stage-9 Verify (or the stage-10 fix-verify re-check) due to a
TRANSIENT toolchain fault — not a real test failure — the harness today treats it as a red verify, enters
the Fix-verify loop, and the coding agent burns iterations "fixing" something it cannot fix; if it never
recovers the task FLAGS. Observed repeatedly driving a SwiftPM target: a build-system race
("input file '…/runner.swift' was modified during the build" → fatalError) and a nested-sandbox fault fail
the test command intermittently; identical re-runs pass. A single deterministic retry distinguishes a
transient fault (passes on retry) from a real failure (fails again), making the verify gate robust to any
flaky target toolchain — entirely general, no toolchain-specific knowledge.

## Current state (researched)

> **Freshness contract:** Locate every anchor by searching for the quoted snippet — never by line number.
> If a snippet no longer exists, treat this state as stale and re-verify before building.

- Stage-9 Verify runs the configured test command via `ITestRunner.RunAsync(rootPath, config.TestCommand, ...)`
  (search `_dependencies.TestRunner.RunAsync` in `src/VisualRelay.Core/Execution/RelayDriver.cs`, the stage-9
  block that also calls `IntegrateGuardAsync` and `GetNewFailuresAsync`). The result feeds `GetNewFailuresAsync`
  / the baseline compare; a failure there is what pushes the task into the red branch (fix-verify / flag).
- `ShellTestRunner` (`src/VisualRelay.Core/Execution/ShellTestRunner.cs`) runs the command via `/bin/sh -lc`
  and returns a result with an exit code / pass-fail. There is no retry today.
- The fix-verify re-check path runs the same test/guard via `RunGuardCheckAsync`
  (`RelayDriver.RepoGuards.cs`) / the stage-10 loop in `RelayDriver.VerifyFix.cs`.

## What to build

Add an opt-out config flag `retryFlakyVerify` (JSON `"retryFlakyVerify"`, default **true**, follow the
`OptionalBool` loader pattern used by the other bool flags in `RelayConfigLoader`). When the test command run
at stage-9 Verify (and the stage-10 fix-verify re-check) reports FAILURE and `retryFlakyVerify` is on, run the
SAME test command ONE more time; use the second result as authoritative. Only if the retry ALSO fails is the
verify red (proceed to fix-verify / flag as today). If the retry passes, treat verify as green.

Keep it general and minimal:
- Retry exactly ONCE (a transient fault clears on a single re-run; do not loop).
- Emit a trace/log event when the retry fires AND when it flips fail→pass (so flaky targets are diagnosable —
  search for the existing `PublishAsync`/event-sink pattern). A silent retry that masks a genuinely flaky suite
  is worse than a visible one.
- Do NOT retry on the baseline run used to compute pre-existing failures (only the post-change verify), and do
  NOT change behavior when `retryFlakyVerify` is false (byte-for-byte unchanged).
- This is about the test COMMAND's own transient faults; it is independent of `maxStallRetries` (which covers
  the LLM subagent) and of the watchdog.

## Tests (TDD)
- A test where the test runner is scripted fail-then-pass and `retryFlakyVerify=true` → the task reaches a
  GREEN verify (no fix-verify entered, outcome Committed). Mirror the existing `ScriptedTestRunner`/recording
  test-double pattern (search `ScriptedTestRunner` in `tests/VisualRelay.Tests/`).
- A test where the runner fails twice → verify is red (fix-verify / flag as before) — proves a real failure
  still blocks.
- A test with `retryFlakyVerify=false` → no retry (single run), behavior unchanged.

## Done when
- A transient (fail-then-pass) verify no longer enters fix-verify and the task commits; a real (fail-then-fail)
  verify still blocks; the flag defaults on and is disablable; a retry emits a visible event. No toolchain-specific
  assumptions.
