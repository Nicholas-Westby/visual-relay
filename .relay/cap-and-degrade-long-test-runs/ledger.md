## Stage 1 - Ideate

{
  "summary": "Add a configurable `testTimeoutMs` (default 300_000 ms / 5 min) that replaces `Timeout.InfiniteTimeSpan` in ShellTestRunner.RunAsync call sites (stages 5 & 9). On expiry, kill the process tree and fail the stage with a structured `TimeoutHint` via `ErrorHintClassifier` that instructs the LLM to fall back to a project-specific targeted subset using the existing `TestFileCommand {files}` mechanism. Cover with a fake-slow-runner test. Address the subagent path by documenting that `subagentTimeoutMs` still governs and updating its timeout message template with subset guidance.",
  "options": [
    "Option A — Minimal wiring: add testTimeoutMs config, pass to ShellTestRunner at call sites, catch TimeoutException, produce TimeoutHint. (~4 files changed, lowest risk, but cap lives in caller not runner.)",
    "Option B — ShellTestRunner-level timeout: change RunAsync signature to accept TimeSpan, return a typed TimeoutResult, driver pattern-matches on it. Cap in the runner itself; all callers protected. (~5 files changed, best balance.)",
    "Option C — Centralized TestRunWatchdog service: decoupled watchdog + event + ErrorHintClassifier subscriber. Subagent decorator injects guidance into prompt. (~7 files changed, cleanest separation but most moving parts.)"
  ]
}

## Stage 2 - Research

{
  "findings": "1. ShellTestRunner.RunAsync (ProcessRunners.cs:14) passes Timeout.InfiniteTimeSpan to ProcessCapture, which already supports finite timeouts and kills process trees (line 213-216). 2. Two call sites in RelayDriver: stage 5 via AuthorTestGate (RelayDriver.cs:111) and stage 9 directly (RelayDriver.cs:144). 3. RelayConfig record (RelayConfig.cs) has no TestTimeoutMilliseconds field yet; must follow SubagentTimeoutMilliseconds naming pattern. 4. RelayConfigLoader.Defaults() (line 8-26) and OptionalInt parsing (line 96) must be extended for testTimeoutMs defaulting to 300_000. 5. ErrorHintClassifier has a generic TimeoutHint (line 16-18) matching 'timed out'/'timeout' substrings; a distinct TestTimeoutHint with subset-guidance is needed. 6. TestRunResult (TestRunResult.cs) has no TimedOut flag — timeout must be surfaced via exit code / message or by enriching the result type. 7. AuthorTestGate.RunAsync catches InvalidOperationException but not timeout; timeouts surface naturally as exit code -1 with distinctive output. 8. Production wiring (MainWindowViewModel.Execution.cs:165) constructs ShellTestRunner with no args; constructor injection of timeout is simplest. 9. ProcessCapture timeout logic (ProcessRunners.cs:213-216) already correctly kills process trees. 10. Subagent timeouts (SwivalSubagentRunner, ProcessRunners.cs:58-64) are separate, governed by subagentTimeoutMs, and already route through ErrorHintClassifier. 11. ScriptedTestRunner (TestDoubles.cs) is synchronous; need a new HangingTestRunner double. 12. Existing timeout tests (VisualRelayTestCommandTimeoutTests.cs) test the bash watchdog, not C# ShellTestRunner. 13. RelayConfigLoaderTests need coverage for the new testTimeoutMs field.",
  "constraints": [
    "All source files must stay under 300 lines",
    "Conventional Commit format required",
    "./visual-relay check must pass (dotnet build+test)",
    "ITestRunner interface may change but should remain minimal (constructor injection preferred over signature change)",
    "Existing fast runs must be unaffected; suite needing longer is configurable",
    "Test doubles must be in TestDoubles.cs or adjacent test support files",
    "Config JSON key should be testTimeoutMs following subagentTimeoutMs pattern",
    "New hint must instruct agent to run a targeted subset using the project-specific mechanism (TestFileCommand {files} pattern), not hardcode a subset strategy",
    "Subagent path (subagentTimeoutMs) timeout message should also carry subset guidance — at minimum document the gap"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "## Root cause: ShellTestRunner has no timeout — passes Timeout.InfiniteTimeSpan\n\n**File: src/VisualRelay.Core/Execution/ProcessRunners.cs, line 14**\n`ShellTestRunner.RunAsync` passes `Timeout.InfiniteTimeSpan` to `ProcessCapture.RunAsync`. This is the single point where the cap is missing. The underlying `ProcessCapture` (lines 213-216) already supports a finite timeout and correctly kills the process tree on expiry — but `ShellTestRunner` never provides a finite value.\n\n**File: src/VisualRelay.Core/Execution/ProcessRunners.cs, line 15**\nThe `TimedOut` bool returned by `ProcessCapture.RunAsync` is **discarded** — `new TestRunResult(result.ExitCode, result.Output)` has no way to carry a timeout signal. `TestRunResult` (src/VisualRelay.Domain/TestRunResult.cs) is a simple `(int ExitCode, string Output)` record with no `TimedOut` field.\n\n## Two unprotected call sites in RelayDriver\n\n**Stage 5 (Author-tests) — RelayDriver.cs:110-118**\nCalls `AuthorTestGate.RunAsync` which invokes `testRunner.RunAsync(rootPath, command, ct)` where command = `config.TestFileCommand.Replace(\"{files}\", ...)`. `AuthorTestGate` (AuthorTestGate.cs:29-33) catches only `InvalidOperationException` — a timeout would surface as exit code -1 with distinctive output but neither the gate nor the driver recognises it as a timeout-vs-failure distinction.\n\n**Stage 9 (Verify) — RelayDriver.cs:144**\nCalls `testRunner.RunAsync(rootPath, config.TestCommand, ct)` directly. Same infinite-timeout path.\n\nBoth passes the cancellation token but the token only fires from the outer run — effectively unbounded.\n\n## Subagent path: 20-minute coarse timeout\n\n**File: src/VisualRelay.Core/Configuration/RelayConfigLoader.cs, line 26**\n`SubagentTimeoutMilliseconds: 1_200_000` — 20 minutes. When swival runs `dotnet test` inside a stage as a tool call and the suite hangs, the subagent process is killed after 20 minutes and the error routes through `ErrorHintClassifier.WithHint` (ProcessRunners.cs:60-64). The timeout hint (ErrorHintClassifier.cs:16-18) says \"Try raising maxTurns/the timeout, or check the model backend's latency\" — generic advice that doesn't address a hung test suite.\n\n## Config gap: no testTimeoutMs field\n\n**File: src/VisualRelay.Domain/RelayConfig.cs**\n14-field positional record. Has `SubagentTimeoutMilliseconds` but no `TestTimeoutMilliseconds`. Naming pattern is established: C# PascalCase `TestTimeoutMilliseconds`, JSON camelCase `testTimeoutMs`.\n\n**File: src/VisualRelay.Core/Configuration/RelayConfigLoader.cs, line 8-26**\n`Defaults()` sets `SubagentTimeoutMilliseconds: 1_200_000` but has no analogous test timeout default. `TryLoadAsync` (line 96) parses `subagentTimeoutMs` via `OptionalInt` but has no `testTimeoutMs` key.\n\n## ErrorHintClassifier has a generic timeout hint — no test-command-specific guidance\n\n**File: src/VisualRelay.Domain/ErrorHintClassifier.cs, lines 16-18**\n`TimeoutHint` matches \"timed out\"/\"timeout\" substrings and suggests \"raising maxTurns/the timeout, or check the model backend's latency.\" This is correct for swival subagent timeouts but wrong for a hung test suite — the remedy is running a targeted subset, not adjusting LLM parameters. A distinct `TestTimeoutHint` is needed with subset-guidance referencing `TestFileCommand {files}`.\n\n## Production wiring — ShellTestRunner constructed with no timeout\n\n**File: src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs, line 165**\n`new ShellTestRunner()` — no constructor parameters. The relay driver never receives the config's timeout value. Options: constructor injection on `ShellTestRunner(TimeSpan timeout)` or passing timeout through `ITestRunner.RunAsync` signature.\n\n## Test infrastructure gap: no hanging test runner double\n\n**File: tests/VisualRelay.Tests/TestDoubles.cs, lines 145-158**\n`ScriptedTestRunner` returns results synchronously — no way to simulate a slow/hanging test. A `HangingTestRunner` double is needed to write a test that verifies the timeout kills the process tree and produces the right hint.\n\n## Run log evidence: run-task3.log (current cap-and-degrade task)\n\n**File: .relay-scratch/run-task3.log**\n- Stage 1 (Ideate, 26s): proposed three options (A: minimal wiring, B: ShellTestRunner-level timeout, C: centralized watchdog). Swival had no filesystem access (sandboxed to `.swival/`) so ideation was based on task description alone.\n- Stage 2 (Research, 51s): read all key source files, confirmed findings 1-12 listed in the Stage 2 output. Identified that `ProcessCapture` already supports timeout+tree-kill (lines 213-216), `ShellTestRunner` discards `TimedOut`, `RelayConfig` needs `TestTimeoutMilliseconds`, `ErrorHintClassifier` needs a distinct `TestTimeoutHint`, and `ScriptedTestRunner` needs a `HangingTestRunner` companion.\n- Stage 3 (Diagnose): currently in progress (this stage).",

  "excerpts": [
    "ProcessRunners.cs:14 — `ShellTestRunner.RunAsync` passes `Timeout.InfiniteTimeSpan` to `ProcessCapture.RunAsync` — the single root cause of unbounded test-command execution.",
    "ProcessRunners.cs:15 — `new TestRunResult(result.ExitCode, result.Output)` discards `result.TimedOut` — timeouts are indistinguishable from failures.",
    "ProcessRunners.cs:213-216 — `ProcessCapture` already supports finite timeout: `if (timeout != Timeout.InfiniteTimeSpan && await Task.WhenAny(waitTask, Task.Delay(timeout)) != waitTask) { process.Kill(entireProcessTree: true); return (-1, output, true); }` — the kill-tree logic is already implemented and correct.",
    "RelayDriver.cs:110-118 — Stage 5 red gate: `await AuthorTestGate.RunAsync(rootPath, taskId, runId, manifest, testFiles, command, _dependencies.TestRunner, cancellationToken)` — no timeout parameter.",
    "RelayDriver.cs:144 — Stage 9 verify: `await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, cancellationToken)` — no timeout parameter.",
    "AuthorTestGate.cs:29-33 — catches only `InvalidOperationException`; a process-killed timeout (exit -1) would fall through as a normal result with no timeout-aware handling.",
    "RelayConfig.cs — 14-field record with `SubagentTimeoutMilliseconds` but no `TestTimeoutMilliseconds` field.",
    "RelayConfigLoader.cs:26 — `SubagentTimeoutMilliseconds: 1_200_000` (20 min) — the only configurable timeout, governing subagent (swival) runs, not driver test-command runs.",
    "RelayConfigLoader.cs:96 — `OptionalInt(root, \"subagentTimeoutMs\", defaults.SubagentTimeoutMilliseconds)` — parsing pattern exists; no `testTimeoutMs` equivalent.",
    "ErrorHintClassifier.cs:16-18 — `TimeoutHint = \"Try raising maxTurns/the timeout, or check the model backend's latency.\"` — generic swival-timeout advice, unsuitable for a hung test suite.",
    "ProcessRunners.cs:60-64 — subagent timeout routes through `ErrorHintClassifier.WithHint(reason)` — pattern to replicate for test-command timeouts.",
    "TestRunResult.cs — `sealed record TestRunResult(int ExitCode, string Output)` — no `TimedOut` field; timeout signal from ProcessCapture is lost.",
    "MainWindowViewModel.Execution.cs:165 — `new ShellTestRunner()` — constructed with no timeout; the config's timeout value never reaches the runner.",
    "TestDoubles.cs:145-158 — `ScriptedTestRunner` returns `Task.FromResult` synchronously — cannot simulate a hanging test for timeout tests."
  ],

  "repro": "1. Create a test suite that hangs (e.g. `dotnet test` with a deadlocked Avalonia headless test, or `sleep 600` as a fake).\n2. Run a Visual Relay task that triggers the red gate (stage 5) or baseline verify (stage 9).\n3. Observe: the `ShellTestRunner` never times out — `Timeout.InfiniteTimeSpan` keeps the process alive until the outer cancellation token fires (which may never fire if the UI is waiting).\n4. If the hang occurs inside a swival subagent that calls `dotnet test` as a tool, the subagent burns the full `subagentTimeoutMs` (default 20 minutes) before `ProcessCapture` kills the swival process tree.\n5. The resulting error message says \"swival timed out after 1200000ms\" with `ErrorHintClassifier` suggesting \"Try raising maxTurns/the timeout, or check the model backend's latency\" — misleading because the problem isn't LLM latency, it's a hung test suite that should be narrowed to a targeted subset.\n6. Expected behavior after fix: the driver passes a finite `testTimeoutMs` (default 300_000 / 5 min) to `ShellTestRunner`. On breach, `ProcessCapture` kills the process tree, `ShellTestRunner` surfaces the timeout through `TestRunResult`, `RelayDriver` fails the stage fast with a `TestTimeoutHint` from `ErrorHintClassifier` instructing the agent to run a targeted subset using `TestFileCommand {files}`."
}

## Stage 4 - Plan

{
  "plan": "## Option B: ShellTestRunner-level timeout (7 production + 5 test files)\n\n### Production changes (7 files)\n\n1. **TestRunResult.cs** — Add `bool TimedOut = false` parameter (backward compatible)\n2. **RelayConfig.cs** — Append `int TestTimeoutMilliseconds` as 15th positional field\n3. **ErrorHintClassifier.cs** — Add `TestTimeoutHint` constant with actionable subset-guidance (pointing at TestFileCommand `{files}`); in `HintFor`, detect \"test command timed out\" BEFORE the generic \"timed out\" check\n4. **RelayConfigLoader.cs** — Default `TestTimeoutMilliseconds: 300_000` in `Defaults()`; parse `testTimeoutMs` JSON key via `OptionalInt` in the `with` block\n5. **ProcessRunners.cs** — `ShellTestRunner` gets constructor `TimeSpan? timeout = null` (defaults to `TimeSpan.MaxValue`/infinite); stores in `_timeout` field; passes to `ProcessCapture.RunAsync` instead of `Timeout.InfiniteTimeSpan`; surfaces `result.TimedOut` into `TestRunResult`. Also update `SwivalSubagentRunner` line 62 timeout reason to include subset-guidance sentence\n6. **RelayDriver.cs** — Stage 5: after `gateResult.TestResult`, if `TimedOut` → `FlagAsync` with `ErrorHintClassifier.WithHint` on timeout message. Stage 9: after `testRunner.RunAsync`, same check. (~10 new lines, stays under 300)\n7. **MainWindowViewModel.Execution.cs** — Pass `TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)` to `new ShellTestRunner(...)` at line 165\n8. **tools/VisualRelay.RunTask/Program.cs** — Same constructor change\n\n### Test changes (4 files)\n\n9. **TestDoubles.cs** — Add `TimeoutSimulatingTestRunner` that returns `TestRunResult(-1, \"test command timed out...\", TimedOut: true)`\n10. **RelayDriverTimeoutTests.cs** (new) — Test that timeout runner causes stage 9 to flag with `TestTimeoutHint` content (RelayDriverTests.cs is at 299 lines)\n11. **ErrorHintClassifierTests.cs** — Add `HintFor_TestCommandTimedOut_ReturnsSubsetGuidance` and `HintFor_GenericTimedOut_StillReturnsTimeoutHint`\n12. **RelayConfigLoaderTests.cs** — Add `LoadAsync_TestTimeoutMs_DefaultsTo300000` and optional override test\n\n### Key design decisions\n- **Option B** chosen: timeout lives in ShellTestRunner constructor, not in `ITestRunner.RunAsync` signature — keeps interface minimal, all callers protected\n- `Timeout.InfiniteTimeSpan` preserved as default for backward compat (no args = no cap)\n- Detection in `ErrorHintClassifier.HintFor` uses \"test command timed out\" substring before the catch-all \"timed out\" — precise routing\n- Subagent path (line 62) gets inline subset-guidance; its `WithHint` call still appends generic `TimeoutHint` (LLM-tuning advice) which is additive, not contradictory\n- Default: 300_000 ms (5 min) — matches the Done criteria\n- No change to `ITestRunner` interface — `TimedOut` is carried on the result record only",
  "manifest": [
    "src/VisualRelay.Domain/TestRunResult.cs",
    "src/VisualRelay.Domain/RelayConfig.cs",
    "src/VisualRelay.Domain/ErrorHintClassifier.cs",
    "src/VisualRelay.Core/Configuration/RelayConfigLoader.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs",
    "tools/VisualRelay.RunTask/Program.cs",
    "tests/VisualRelay.Tests/TestDoubles.cs",
    "tests/VisualRelay.Tests/RelayDriverTimeoutTests.cs",
    "tests/VisualRelay.Tests/ErrorHintClassifierTests.cs",
    "tests/VisualRelay.Tests/RelayConfigLoaderTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/TestDoubles.cs",
    "tests/VisualRelay.Tests/RelayDriverTimeoutTests.cs",
    "tests/VisualRelay.Tests/ErrorHintClassifierTests.cs",
    "tests/VisualRelay.Tests/RelayConfigLoaderTests.cs"
  ],
  "rationale": "4 test files covering the target behavior. All fail before implementation:\n\n1. **TestDoubles.cs** — `TimeoutSimulatingTestRunner` returns `TestRunResult(-1, output, TimedOut: true)`. Fails to compile (CS1739) because `TestRunResult` has no `TimedOut` field yet.\n\n2. **RelayDriverTimeoutTests.cs** (new) — End-to-end test: a `ScriptedTestRunner` returns red at stage 5 then `TimedOut: true` at stage 9. Asserts the driver flags with the `TestTimeoutHint` subset-guidance (\"targeted subset\", \"{files}\", \"300000ms\") rather than a generic \"verify failed\". Fails to compile (CS1739) because `TestRunResult` lacks `TimedOut`. Even if it compiled, the driver has no `TimedOut` check at stage 9.\n\n3. **ErrorHintClassifierTests.cs** — Two new tests:\n   - `HintFor_TestCommandTimedOut_ReturnsSubsetGuidance`: asserts \"test command timed out after 300000ms\" returns a hint containing \"targeted subset\" and \"{files}\" and NOT \"maxTurns\"/\"model backend\". Currently fails at runtime because `HintFor` matches the generic \"timed out\" substring first and returns the LLM-tuning `TimeoutHint`.\n   - `HintFor_GenericTimedOut_StillReturnsTimeoutHint`: asserts \"swival timed out after 600000ms\" still returns the generic `TimeoutHint` mentioning \"maxTurns\". Regression guard — passes now, must keep passing after implementation.\n\n4. **RelayConfigLoaderTests.cs** — Two new tests:\n   - `LoadAsync_TestTimeoutMs_DefaultsTo300000`: asserts config without `testTimeoutMs` key defaults to 300_000. Fails to compile (CS1061) because `RelayConfig` lacks `TestTimeoutMilliseconds`.\n   - `TryLoadAsync_TestTimeoutMs_Override`: asserts `testTimeoutMs: 600000` in JSON maps to `TestTimeoutMilliseconds: 600_000`. Fails to compile (CS1061) for the same reason.\n\nAll errors are the expected TDD red state: compile errors for missing types/members (TestRunResult.TimedOut, RelayConfig.TestTimeoutMilliseconds) and runtime assertion failures for the HintFor ordering (test-command-timeout detection must precede the generic timeout catch-all)."
}

## Stage 6 - Implement

{
  "summary": "Implemented cap-and-degrade-long-test-runs (Option B: ShellTestRunner-level timeout).\n\n**Changes across 12 files (7 production + 5 test):**\n\n1. **TestRunResult.cs** — Added `bool TimedOut = false` parameter to carry the timeout signal from ProcessCapture through to the driver.\n\n2. **RelayConfig.cs** — Added `int TestTimeoutMilliseconds` as the 15th positional field.\n\n3. **ErrorHintClassifier.cs** — Added `TestTimeoutHint` constant with actionable subset-guidance pointing at `TestFileCommand \"{files}\"` pattern. Added \"test command timed out\" detection BEFORE the generic \"timed out\" check so the correct hint routes for hung test suites vs. swival/LLM timeouts.\n\n4. **RelayConfigLoader.cs** — Defaults `TestTimeoutMilliseconds: 300_000` (5 min). Parses `testTimeoutMs` JSON key via `OptionalInt`.\n\n5. **ProcessRunners.cs** — `ShellTestRunner` now accepts `TimeSpan? timeout = null` (defaults to `Timeout.InfiniteTimeSpan` for backward compat). Passes it to `ProcessCapture.RunAsync` (which already supports finite timeouts and process-tree kill). Surfaces `result.TimedOut` into `TestRunResult`. Updated subagent timeout reason to include subset-guidance sentence.\n\n6. **RelayDriver.cs** — Stage 5 (red gate): after `AuthorTestGate`, checks `testResult.TimedOut` and flags with `ErrorHintClassifier.WithHint`. Stage 9 (verify): same TimedOut check before exit-code evaluation. Both now fail fast with actionable subset-guidance instead of hanging or producing generic \"verify failed\".\n\n7. **MainWindowViewModel.Execution.cs** — Passes `TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)` to `ShellTestRunner`.\n\n8. **Program.cs** (RunTask) — Same constructor change.\n\n9. **TestDoubles.cs** — Already had `TimeoutSimulatingTestRunner` returning `TestRunResult(-1, output, TimedOut: true)`.\n\n10. **RelayDriverTimeoutTests.cs** (new) — E2E test: `ScriptedTestRunner` returns red at stage 5 then `TimedOut: true` at stage 9; asserts driver flags with subset-guidance (\"targeted subset\", \"{files}\", \"300000ms\").\n\n11. **ErrorHintClassifierTests.cs** — `HintFor_TestCommandTimedOut_ReturnsSubsetGuidance` and `HintFor_GenericTimedOut_StillReturnsTimeoutHint`.\n\n12. **RelayConfigLoaderTests.cs** — `LoadAsync_TestTimeoutMs_DefaultsTo300000` and `TryLoadAsync_TestTimeoutMs_Override`.\n\n**Result:** `./visual-relay check` green (build + 221 tests pass). All modified files under 300 lines. Subagent path (subagentTimeoutMs) timeout message now includes subset-guidance; its `WithHint` call still appends generic `TimeoutHint` additively."
}

## Stage 7 - Review

{
  "verdict": "changes",
  "issues": [
    {
      "severity": "blocking",
      "file": "src/VisualRelay.Core/Execution/ProcessRunners.cs",
      "lines": "19-23",
      "summary": "ShellTestRunner.RunAsync passes raw ProcessCapture output through to TestRunResult unchanged when TimedOut is true. The raw output is the killed process's stdout/stderr (e.g. partial dotnet test noise), which never contains the substring 'test command timed out' that ErrorHintClassifier.HintFor (line 47) depends on to return TestTimeoutHint. Result: production timed-out runs get no subset-guidance appended — the error is silent/opaque.",
      "fix": "In ShellTestRunner.RunAsync, when result.TimedOut is true, prepend a classifier-friendly message to the output before constructing TestRunResult — e.g. $\"test command timed out after {_timeout.TotalMilliseconds:F0}ms\\n\\n{result.Output}\". Alternatively, construct the message in RelayDriver at the two TimedOut check sites (lines 131-134 and 151-154) before calling WithHint. The TimeoutSimulatingTestRunner double already models the correct output format; the real ShellTestRunner should produce output consistent with that contract."
    }
  ]
}

## Stage 8 - Fix

{ "summary": "Fixed the Stage 7 blocking issue: ShellTestRunner.RunAsync now prepends \"test command timed out after {ms}ms\\n\\n\" to output when TimedOut is true, so ErrorHintClassifier.HintFor can match \"test command timed out\" and return TestTimeoutHint (subset-guidance). Previously raw ProcessCapture output was passed through unchanged, so production timeouts never surfaced the actionable hint. Single-file change (ProcessRunners.cs:22-24), 3 lines added, 235 lines total. ./visual-relay check green, all 221 tests pass." }

## Stage 9 - Verify

{
  "summary": "Implemented a configurable test-command timeout cap (default 5 min / 300_000 ms) that prevents hangs in stages 5 and 9 by replacing `Timeout.InfiniteTimeSpan` in `ShellTestRunner` with a finite cap passed from `RelayConfig.TestTimeoutMilliseconds`. On breach, `ProcessCapture` kills the process tree, `ShellTestRunner` prepends \"test command timed out after {ms}ms\" to the output, and `RelayDriver` flags the stage via `ErrorHintClassifier.WithHint`, which routes to the new `TestTimeoutHint` (subset-guidance mentioning the `TestFileCommand \"{files}\"` pattern) ahead of the generic `TimeoutHint`. The subagent path (`SwivalSubagentRunner`) also now includes subset-guidance in its timeout message. Seven production files changed (`TestRunResult.cs`, `RelayConfig.cs`, `ErrorHintClassifier.cs`, `RelayConfigLoader.cs`, `ProcessRunners.cs`, `RelayDriver.cs`, `MainWindowViewModel.Execution.cs`, `Program.cs`), five test files (`TestDoubles.cs`, new `RelayDriverTimeoutTests.cs`, `ErrorHintClassifierTests.cs`, `RelayConfigLoaderTests.cs`). All 221 tests pass.",
  "commitMessages": [
    "feat: cap test-command execution at configurable timeout, degrade to targeted subset on halt",
    "feat(driver): replace InfiniteTimeSpan with 5-min configurable test timeout, surface subset-guidance hint",
    "fix: prevent hung test suites from wedging Visual Relay — cap at testTimeoutMs (default 300s) with actionable ErrorHintClassifier guidance",
    "feat: add TestTimeoutMilliseconds to RelayConfig, wire through ShellTestRunner and ErrorHintClassifier for fast fail on hang"
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "Stage 10 Fix-verify confirms the pinned suite is green: 221 tests pass (0 failures), build with 0 errors/0 warnings, ./visual-relay check green, all 12 manifest files under 300 lines (max 296). Every 'Done when' criterion satisfied — configurable testTimeoutMs (default 300s) replaces InfiniteTimeSpan, ProcessCapture kills the process tree on breach, ShellTestRunner surfaces the timeout via TestRunResult.TimedOut and prepends a classifier-friendly message, RelayDriver flags with ErrorHintClassifier.TestTimeoutHint (subset-guidance referencing TestFileCommand \"{files}\" pattern), SwivalSubagentRunner timeout message includes inline subset-guidance. No failures to fix."
}

## Stage 11 - Commit

Committed by Visual Relay.

