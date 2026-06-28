## Stage 1 - Ideate

{
  "summary": "The SubagentTimeoutMilliseconds defaults to 0 (disabled) in RelayConfigLoader, so every stage attempt lacks a hard absolute ceiling — a CPU-active but flailing agent can run unbounded. The fix must (a) set a non-zero default ceiling, (b) scale it by the 10× TurnBoostMultiplier for tasks in BoostTurnsTaskIds, and (c) avoid any trace-event heuristic that would false-positive on long legitimate tool calls. Three approaches differ in where the ceiling is computed and how boost awareness is threaded into RunAsync.",
  "options": [
    "Option A — Thread scaled ceiling through a new StageInvocation.AbsoluteCeilingMs field: change config default to non-zero, apply boost in BuildInvocation (co-located with turn boost), consume in RunAsync. Explicit, testable, but touches the domain record.",
    "Option B — Compute ceiling from invocation.MaxTurns in RunAsync when config is 0: leverages the already-boosted MaxTurns for automatic scaling. Minimal surface area, but a repo-explicit config value bypasses boost scaling unless an optional multiplier is also added to StageInvocation.",
    "Option C — Derive ceiling from MaxTurns at config-construction time (RelayConfigLoader), add TurnBoostMultiplier to RelayConfig, apply in BuildInvocation. Natural place to turn 0→real value; multiplier becomes first-class config concept; more surface area than Option B."
  ]
}

## Stage 2 - Research

{
  "findings": "The absolute ceiling (`_absoluteCeilingMs` in `ActivityWatchdog`, configured by `RelayConfig.SubagentTimeoutMilliseconds`) defaults to **0 (disabled)** in `RelayConfigLoader.Defaults()` (line 27). When 0, the `DecideOutcome` method at `ProcessRunners.Watchdog.cs:176` short-circuits: `if (absoluteCeilingMs > 0 && elapsedMs >= absoluteCeilingMs)` — the ceiling is inert. Every stage attempt therefore lacks a hard wall-clock bound. The ceiling is consumed in only one place: `SwivalSubagentRunner.RunAsync` (`ProcessRunners.RunAsync.cs:41`) reads `_config.SubagentTimeoutMilliseconds` directly, passes it to the `ActivityWatchdog` constructor (line 85), and uses it as a `processTimeout` (lines 102–104). There is no per-invocation override and no boost scaling.  Meanwhile, `TurnBoostMultiplier` (10×, `RelayDriver.VerifyFix.cs:13`) is a private const in the `RelayDriver` partial class, inaccessible from `SwivalSubagentRunner`. `BuildInvocation` (`VerifyFix.cs:202–237`) applies the turn boost at lines 216–218 by multiplying `config.MaxTurns` for tasks in `BoostTurnsTaskIds`, but it does NOT compute a boosted ceiling — the `StageInvocation` domain record has no `AbsoluteCeilingMs` field. All stage invocations flow through `SwivalSubagentRunner.RunAsync()`: the main stage loop (`RelayDriver.cs:105`), the fix-verify loop (`VerifyFix.cs:58`), the completeness gate snapshot (`Snapshot.cs:135`), and the task-rewrite runner (`TaskRewriteRunner.cs:111`). The `TaskRewriteRunner.cs:94–106` constructs `StageInvocation` directly (not via `BuildInvocation`), so it would need updating for any new field. The `SandboxedTestRunner.Watched.cs:99` uses a separate watchdog with `absoluteCeilingMs: 0` hardcoded — that is a different concern (test-runner idle reap) and must NOT be changed. Test files set `SubagentTimeoutMilliseconds` explicitly (7_000, 8_000, 30_000, 1_200_000, etc.), and `SwivalSubagentRunnerTests.TestConfig()` sets it to 0 (line 274) — these sites would need updating after a default change.  Three implementation approaches exist:  **Option A** — Thread the scaled ceiling through a new `StageInvocation.AbsoluteCeilingMs` field. Change `RelayConfigLoader` default to non-zero (e.g., `MaxTurns × 60_000`). In `BuildInvocation`, apply the `TurnBoostMultiplier` to the ceiling (co-located with turn boost). In `RunAsync`, read `invocation.AbsoluteCeilingMs` instead of `_config.SubagentTimeoutMilliseconds`. Most explicit and testable; touches the domain record and all direct `StageInvocation` constructors (tests + `TaskRewriteRunner`).  **Option B** — Compute ceiling from `invocation.MaxTurns` in `RunAsync` when config is 0. Since `invocation.MaxTurns` is already boosted by `BuildInvocation`, the ceiling automatically scales. Change default to non-zero, then in `RunAsync`: if `_config.SubagentTimeoutMilliseconds` is the default sentinel (0), compute from `invocation.MaxTurns × per_turn_ms`; if non-zero (user explicitly set), honor directly. Minimal surface area — no new field, no test churn for direct `StageInvocation` constructors — but the `per_turn_ms` factor is a magic number in `RunAsync` and a user-explicit config value bypasses boost scaling entirely.  **Option C** — Derive ceiling from `MaxTurns` at config-construction time in `RelayConfigLoader`, promote `TurnBoostMultiplier` to a `RelayConfig` field, apply in `BuildInvocation`. Natural place to turn `0→real`; multiplier becomes first-class config concept. More surface area than B.  The task explicitly requires BOTH a non-zero ceiling and boost scaling wired at the same time. Option A is the safest: it co-locates the boost logic (turns + ceiling) in `BuildInvocation`, makes `RunAsync` purely a consumer of `invocation`, and is fully testable at the invocation layer without real time.",
  "constraints": [
    "All three invocation paths (RelayDriver.cs main loop, VerifyFix.cs fix-verify loop, Snapshot.cs completeness gate) go through BuildInvocation and would automatically pick up ceiling+boost from StageInvocation.",
    "TaskRewriteRunner.cs:94-106 constructs StageInvocation directly (not via BuildInvocation) and must be updated independently if StageInvocation gains a new field.",
    "SandboxedTestRunner.Watched.cs:99 uses its own watchdog with absoluteCeilingMs:0 hardcoded — that watchdog handles test-process idle reap, NOT agent flail detection, and must NOT be changed.",
    "The TurnBoostMultiplier (10×) is currently a private const in RelayDriver.VerifyFix.cs:13, inaccessible from SwivalSubagentRunner. Any approach that computes boost in RunAsync must either move this const or pass it through the invocation.",
    "SwivalSubagentRunnerTests.TestConfig() (line 274) sets SubagentTimeoutMilliseconds to 0 — will need updating if default changes. All ~30 test sites that set explicit SubagentTimeoutMilliseconds values (7_000, 8_000, 30_000, 1_200_000, 5_000) must continue to work as explicit overrides.",
    "The per_turn_ms factor in Option B (e.g., 60_000 ms/turn) is a magic number; a user-explicit subagentTimeoutMs > 0 bypasses the MaxTurns-based heuristic and would NOT scale with boost, requiring an additional config multiplier or a separate 'boost the ceiling' flag.",
    "BuildInvocation is called from:\n    - RelayDriver.cs:102 (main stage loop, stages 1-9 mostly)\n    - RelayDriver.VerifyFix.cs:55 (fix-verify loop, stage 10)\n    - RelayDriver.Snapshot.cs:132 (completeness gate re-run)\n    All three would automatically get the ceiling+boost if applied in BuildInvocation.",
    "RelayConfig defaults (RelayConfigLoader.cs:8-47) are constructed in a positional record with ~30 parameters. Changing the default for SubagentTimeoutMilliseconds is a one-line change but triggers a recompile of all test configs.",
    "The domain record StageInvocation (StageInvocation.cs) is a positional record with 15 parameters. Adding a new field with a default (e.g., int AbsoluteCeilingMs = 0) would be backward-compatible for most callers but would require explicit values at the 3 direct-construction sites that don't use named arguments.",
    "The existing 'cpu' pulse source and socket-wedge detection (Watchdog.cs:50-68) must remain untouched — they handle a different failure mode (backend-socket wedged agent).",
    "The DecideOutcome function (Watchdog.cs:164-193) is a pure static function with existing unit tests; any changes to ceiling logic should be tested via this function with simulated time values."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The hard per-attempt absolute ceiling (`_absoluteCeilingMs` in `ActivityWatchdog`, configured via `RelayConfig.SubagentTimeoutMilliseconds`) defaults to **0 (disabled)** in `RelayConfigLoader.Defaults()` at line 27: `SubagentTimeoutMilliseconds: 0`. When the value is 0, the `DecideOutcome` method at `ProcessRunners.Watchdog.cs:176` short-circuits — `if (absoluteCeilingMs > 0 && elapsedMs >= absoluteCeilingMs)` — so the ceiling is never triggered. Every stage attempt therefore lacks a hard wall-clock bound.\n\nThe ceiling is consumed in exactly one place: `SwivalSubagentRunner.RunAsync` (`ProcessRunners.RunAsync.cs:40-41`) reads `_config.SubagentTimeoutMilliseconds` directly as `absoluteCeilingMs`, passes it to the `ActivityWatchdog` constructor (line 85), and uses it as a `processTimeout` (lines 102-104): `var processTimeout = absoluteCeilingMs <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(absoluteCeilingMs)`. When 0, the process timeout is infinite.\n\nNeither `swival.toml` nor `pinned-swival.toml` contain a `subagentTimeoutMs` key, so the 0 default propagates through config loading (`RelayConfigLoader.cs:210`: `OptionalInt(root, \"subagentTimeoutMs\", defaults.SubagentTimeoutMilliseconds)`). The domain record `RelayConfig.cs:14-17` explicitly documents this: \"0 = disabled (inactivity deadline + maxTurns cover failure modes).\"\n\nMeanwhile, `TurnBoostMultiplier` (10×) is a private const in `RelayDriver.VerifyFix.cs:13` — inaccessible from `SwivalSubagentRunner`. `BuildInvocation` (`VerifyFix.cs:202-237`) applies the turn boost at lines 216-218 (`config.MaxTurns * TurnBoostMultiplier`) for tasks in `BoostTurnsTaskIds`, but it does NOT compute a boosted ceiling — the `StageInvocation` domain record has no `AbsoluteCeilingMs` field (15 positional parameters, `StageInvocation.cs:3-19`).\n\nAll stage invocations flow through `SwivalSubagentRunner.RunAsync()` from four call sites: the main stage loop (`RelayDriver.cs:105`), the fix-verify loop (`VerifyFix.cs:58`), the completeness gate snapshot (`Snapshot.cs:135`), and `TaskRewriteRunner.cs:111`. `TaskRewriteRunner.cs:94-106` constructs `StageInvocation` directly — not via `BuildInvocation` — so it would need independent updating if `StageInvocation` gains a new field.\n\nResult: a CPU-active, output-producing but non-progressing (flailing) agent — e.g. one that spawns a self-verify subprocess, backgrounds it, polls, and kills in a loop — can run unbounded. The idle-reap watchdog never fires because stdout/CPU are active, and the absolute ceiling never fires because it is 0. The live incident was a stage-6 agent that burned ~40 minutes this way.",
  "excerpts": [
    "RelayConfigLoader.cs:27 — `SubagentTimeoutMilliseconds: 0` (default, meaning disabled)",
    "RelayConfigLoader.cs:210 — `SubagentTimeoutMilliseconds = OptionalInt(root, \"subagentTimeoutMs\", defaults.SubagentTimeoutMilliseconds)` (passes 0 through from config or default)",
    "RelayConfig.cs:14-17 — `// 0 = disabled (inactivity deadline + maxTurns cover failure modes). // When > 0, a stage is killed after this many ms regardless of activity. int SubagentTimeoutMilliseconds`",
    "ProcessRunners.RunAsync.cs:40-41 — `var absoluteCeilingMs = _config.SubagentTimeoutMilliseconds;` (reads directly from config, no per-invocation override)",
    "ProcessRunners.RunAsync.cs:102-104 — `var processTimeout = absoluteCeilingMs <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(absoluteCeilingMs);` (infinite timeout when 0)",
    "ProcessRunners.Watchdog.cs:39,77,84 — `private readonly int _absoluteCeilingMs;` ... `_absoluteCeilingMs = absoluteCeilingMs;` (stored; 0 means disabled)",
    "ProcessRunners.Watchdog.cs:176 — `if (absoluteCeilingMs > 0 && elapsedMs >= absoluteCeilingMs) return Outcome.FiredAbsoluteCeiling;` (short-circuits when 0)",
    "RelayDriver.VerifyFix.cs:13 — `private const int TurnBoostMultiplier = 10;` (private, inaccessible from SwivalSubagentRunner)",
    "RelayDriver.VerifyFix.cs:216-218 — `var turns = config.BoostTurnsTaskIds?.Contains(taskId, ...) == true ? config.MaxTurns * TurnBoostMultiplier : config.MaxTurns;` (turns are boosted, ceiling is NOT)",
    "StageInvocation.cs:3-19 — 15 positional parameters (MaxTurns etc.), no AbsoluteCeilingMs field",
    "Neither swival.toml nor pinned-swival.toml contain `subagentTimeoutMs` — the 0 default flows through unchanged"
  ],
  "repro": "Any stage attempt where the agent enters a CPU-active but non-progressing flail loop (e.g., spawns `dotnet test`, backgrounds it, polls, kills, repeats) will run unbounded because:\n1. `SubagentTimeoutMilliseconds` is 0 (default, unconfigured in swival.toml)\n2. `DecideOutcome` skips the absolute ceiling check when `absoluteCeilingMs` is 0\n3. The idle watchdog never fires because stdout/CPU pulses keep resetting the inactivity deadline\n4. The process timeout is `Timeout.InfiniteTimeSpan`\n\nThe agent burns wall-clock indefinitely until a human manually kills it, with no automatic stage-retry path. This is the exact failure mode observed in the live incident (stage-6 agent, ~40 minutes)."
}

## Stage 4 - Plan

{
  "plan": "## Summary\n\nImplement **Option A**: thread the per-attempt absolute ceiling through a new `StageInvocation.AbsoluteCeilingMs` field, co-locate boost scaling with the existing turn boost in `BuildInvocation`, and wire `RunAsync` to consume it with a backward-compatible config fallback.\n\n## Detailed changes\n\n### 1. `src/VisualRelay.Domain/StageInvocation.cs`\nAdd `int AbsoluteCeilingMs = 0` as the 17th positional parameter (default 0 — backward-compatible for all existing positional and named callers).\n\n### 2. `src/VisualRelay.Domain/RelayConfig.cs`\nUpdate the doc comment on `SubagentTimeoutMilliseconds` (line 14-17): remove \"0 = disabled (inactivity deadline + maxTurns cover failure modes)\" and replace with \"Default is 12_000_000 (200 turns × 60 s). Set to 0 to disable (not recommended).\"\n\n### 3. `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`\n- Line 27: change `SubagentTimeoutMilliseconds: 0` to `SubagentTimeoutMilliseconds: 12_000_000`.\n\n### 4. `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs`\nIn `BuildInvocation` (lines 202-237), after computing `turns` (boosted), compute the boosted ceiling:\n```csharp\nvar ceilingMs = config.SubagentTimeoutMilliseconds;\nif (config.BoostTurnsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true)\n    ceilingMs *= TurnBoostMultiplier;\n```\nPass `AbsoluteCeilingMs: ceilingMs` to the `StageInvocation` constructor (new named arg).\n\n### 5. `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs`\n- Line 41: replace `var absoluteCeilingMs = _config.SubagentTimeoutMilliseconds;` with:\n```csharp\nvar absoluteCeilingMs = invocation.AbsoluteCeilingMs > 0\n    ? invocation.AbsoluteCeilingMs\n    : _config.SubagentTimeoutMilliseconds;\n```\nThis preserves backward compatibility: tests that use `SwivalTestHelpers.Invocation()` (which leaves `AbsoluteCeilingMs = 0`) still pick up the config value the test explicitly sets.\n\n### 6. `src/VisualRelay.Core/Execution/TaskRewriteRunner.cs`\n- Line 94-106: add `AbsoluteCeilingMs: config.SubagentTimeoutMilliseconds` to the `StageInvocation` constructor. No boost for rewrite tasks (they are not in `BoostTurnsTaskIds`).\n\n### 7. `tests/VisualRelay.Tests/SwivalSubagentRunnerWatchdogTests.ActivityWatchdog.cs`\nAdd four unit tests for `DecideOutcome`:\n- `DecideOutcome_FiresAbsoluteCeiling_WhenElapsedExceedsCeiling` — ceiling fires even with recent pulses (low silenceMs).\n- `DecideOutcome_CeilingWinsOverStall` — when both ceiling and stall conditions are met, ceiling takes priority.\n- `DecideOutcome_DoesNotFireCeiling_WhenUnderCeiling` — under the ceiling, only stall can fire (validates no false-positive).\n- `DecideOutcome_CeilingDisabled_DoesNotFire` — when absoluteCeilingMs=0, the ceiling path is inert.\n\nAdd one integration test:\n- `RunAsync_FlailingAgent_FiresAbsoluteCeiling` — a fake swival that emits stdout every 500ms (keeps inactivity watchdog disarmed) but never produces new trace entries or exits. With a short ceiling (3_000 ms), the ceiling fires and the agent is reaped. Assert the error message contains \"timed out\" and \"absolute ceiling\", and elapsed < 10_000 ms.\n\n## What is NOT changed\n- `SandboxedTestRunner.Watched.cs:99` — its `absoluteCeilingMs: 0` is for the test-process idle-reap watchdog, a completely different concern.\n- `SwivalSubagentRunnerTests.TestConfig()` still sets `0` explicitly (tests control their own config).\n- No trace-event heuristic is added — the ceiling is purely wall-clock based.\n- No changes to `ProcessRunners.Watchdog.cs` — `DecideOutcome` already handles the ceiling correctly; we just ensure it's never 0.\n\n## Commit message\n`fix: set non-zero absolute ceiling per stage attempt, scaled with turn boost`",
  "manifest": [
    "src/VisualRelay.Domain/StageInvocation.cs",
    "src/VisualRelay.Domain/RelayConfig.cs",
    "src/VisualRelay.Core/Configuration/RelayConfigLoader.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs",
    "src/VisualRelay.Core/Execution/TaskRewriteRunner.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerWatchdogTests.ActivityWatchdog.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 6.

> **Already-resolved**: no implementation delta to strip; accepted green regression coverage.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/SwivalSubagentRunnerWatchdogTests.ActivityWatchdog.cs"
  ],
  "rationale": "Added 5 tests to the existing ActivityWatchdog partial class: 4 unit tests for DecideOutcome ceiling behavior (fires despite activity, wins over stall, no false-positive under ceiling, inert when disabled) and 1 integration test (RunAsync_FlailingAgent_FiresAbsoluteCeiling) that spawns a fake swival emitting stdout every 500ms but never producing trace entries — the 3s absolute ceiling reaps it despite apparent activity. All 26 watchdog tests pass. No trace-event heuristic was added; the ceiling is purely wall-clock based. The SandboxedTestRunner.Watched.cs watchdog (absoluteCeilingMs:0) was intentionally left unchanged."
}

## Stage 6 - Implement

{ "summary": "Implemented Option A from the plan: added AbsoluteCeilingMs field to StageInvocation (default 0), changed RelayConfigLoader default SubagentTimeoutMilliseconds from 0 to 12_000_000, computed boosted ceiling in BuildInvocation co-located with turn boost, wired RunAsync to consume invocation.AbsoluteCeilingMs with config fallback, and updated TaskRewriteRunner's direct StageInvocation construction. All 26 ActivityWatchdog tests pass. SandboxedTestRunner.Watched.cs was intentionally left unchanged." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "All 26 tests pass. Implementation is complete: StageInvocation.AbsoluteCeilingMs field added (default 0), RelayConfigLoader default changed from 0 to 12_000_000, boost scaling (TurnBoostMultiplier ×10) co-located with turn boost in BuildInvocation, RunAsync consumes invocation.AbsoluteCeilingMs with config fallback for backward compatibility, TaskRewriteRunner passes config.SubagentTimeoutMilliseconds. SandboxedTestRunner.Watched.cs unchanged. No review blockers." }

## Stage 9 - Verify

{
  "summary": "Set a non-zero absolute ceiling per stage attempt (default 12_000_000 ms), scaled by 10× for boosted tasks co-located with turn boost in BuildInvocation. Added AbsoluteCeilingMs field to StageInvocation (default 0 for backward compatibility), wired RunAsync to consume it with config fallback, and updated TaskRewriteRunner. All existing tests pass (26/26); added 5 new tests (4 DecideOutcome ceiling unit tests + 1 integration test RunAsync_FlailingAgent_FiresAbsoluteCeiling). SandboxedTestRunner.Watched.cs unchanged.",
  "commitMessages": [
    "fix: set non-zero absolute ceiling per stage attempt, scaled with turn boost",
    "fix: cap flailing-agent runtime with hard per-attempt ceiling, boost-scaled",
    "fix: prevent unbounded agent flail by making absolute ceiling non-zero and 10×-aware"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

