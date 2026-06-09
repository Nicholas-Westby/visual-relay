## Stage 1 - Ideate

{
  "summary": "DrainCircuitBreaker currently halts on the first flagged/stalled task. The fix generalizes the consecutive-flag counter to all flag types (not just commit-rejects), so an isolated flag sets the task aside as NeedsReview and continues; the drain only halts when a threshold of consecutive flags (with no successful commit resetting the counter) is exceeded. Three approaches: (1) dual-threshold — keep a separate lower threshold for commit-rejects alongside a general flag threshold (most aligned with the task's suggested approach); (2) unified single-threshold — one counter for all flags, simplest; (3) per-category dictionary — extensible but over-engineered for the current two sub-types.",
  "options": [
    "Dual-threshold generalization: _consecutiveFlags counter (threshold 3) for all flags + existing _consecutiveCommitRejects (threshold 2) for commit-rejected tasks. Reset both on Committed.",
    "Unified single-threshold: one _consecutiveFlags counter with ConsecutiveFlagThreshold=3 covering all flag types, including commit-rejects. Simplest code, loses the faster trip for commit-rejects.",
    "Configurable per-category thresholds: Dictionary<FlagCategory,int> with categories for CommitRejected, Timeout, Other. Most flexible but over-engineered for current needs."
  ]
}

## Stage 2 - Research

{
  "findings": "DrainCircuitBreaker halts on first non-commit-reject flag via `else if (outcome.Status == Flagged)` returning true immediately. No general consecutive-flag counter exists. `RelayQueueController.DrainAsync` snapshots `Tasks.Where(!NeedsReview)` at line 72, removes each from `Tasks` at line 79, checks `ShouldHalt` at line 81, and returns early on halt leaving remaining tasks unrun. `MainWindowViewModel.DrainQueueAsync` duplicates this pattern with its own `DrainCircuitBreaker` and `RelayDriver`-based `RunOneAsync`. NEEDS-REVIEW markers are written by `RelayDriver.FlagAsync` on disk; test fakes don't write them. `RelayQueueState` has `ReviewNeeded`, `Failed`, `Completed` terminal states but no `CompletedWithFlags`. Existing tests verify halt-on-first-flag and commit-reject-threshold behaviors.",
  "constraints": [
    "Line guard for DrainCircuitBreaker.cs (~56 lines) and RelayQueueController.cs (~112 lines) — additions must be concise.",
    "Backward compatibility of RelayQueueState enum — new state optional but must not break consumers.",
    "NEEDS-REVIEW marker must be written to disk (or in-memory task updated) so subsequent RefreshAsync loads flagged tasks as NeedsReview; test fakes don't write it.",
    "Two independent drain paths (RelayQueueController.DrainAsync with injected IRelayTaskRunner, and MainWindowViewModel.DrainQueueAsync with real RelayDriver) both need updating — DrainCircuitBreaker changes affect both, but loop structure changes needed in both files.",
    "Snapshot isolation in DrainAsync must be preserved — tasks added mid-drain are not processed.",
    "If keeping commit-reject dual threshold, it must be a separate counter reset by non-commit-reject flags, while general flag counter increments across all flag types; Committed resets both.",
    "Existing test DrainAsync_HaltsAtFirstTaskThatNeedsReview must be rewritten for new continue-past-flag behavior.",
    "DRAIN-HALTED marker must only be written on actual threshold-based halt, not on every flagged task.",
    "UI StatusText messages in MainWindowViewModel.DrainQueueAsync need updating for new outcome states (e.g. 'completed with N flagged')."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "DrainCircuitBreaker.ShouldHalt returns true on the first non-commit-reject Flagged outcome via the else-if branch at line 26. There is no general consecutive-flag counter — only _consecutiveCommitRejects exists. Any Flagged outcome whose Reason does not start with \"commit rejected:\" (including stalled/timeout tasks with reasons like \"swival timed out after…\" or \"author-tests did not go red\") triggers an immediate halt. DrainAsync at line 81 then returns early, abandoning all remaining pending tasks in the snapshot queue.",
  "excerpts": [
    "DrainCircuitBreaker.cs:26-31: `else if (outcome.Status == RelayTaskOutcomeStatus.Flagged) { … return true; }` — halts on first non-commit-reject flag",
    "DrainCircuitBreaker.cs:9: `private int _consecutiveCommitRejects;` — only counter that exists; no `_consecutiveFlags` or `ConsecutiveFlagThreshold`",
    "RelayQueueController.cs:72-87: snapshot loop calls ShouldHalt, returns early on true → remaining queue items never run",
    "ProcessRunners.cs:70-80: SwivalSubagentRunner timeout produces reason strings starting with \"swival\" (not \"commit rejected:\")",
    "RelayDriver.cs:95-97: `!result.IsValid` → `FlagAsync(…, result.Error)` → Flagged outcome with timeout reason",
    "RelayQueueControllerTests.cs:51-67: DrainAsync_HaltsAtFirstTaskThatNeedsReview encodes broken behavior as expected",
    "MainWindowViewModel.Execution.cs:73-91: duplicate halt-on-first-flag in UI drain path"
  ],
  "repro": "1. Create 3 pending tasks (alpha, beta, gamma).\n2. Use a FlaggingTaskRunner that returns Flagged for alpha (reason: \"author-tests did not go red\") and Committed for beta/gamma.\n3. Call DrainAsync.\n4. Observe: only alpha runs; State = ReviewNeeded; beta and gamma are abandoned.\n5. Expected after fix: all 3 run; alpha is set aside as NeedsReview; State = Completed (or CompletedWithFlags); beta and gamma are committed."
}

## Stage 4 - Plan

{
  "plan": "## Summary\n\nGeneralize DrainCircuitBreaker to track consecutive flags (any reason), not just commit-rejects. An isolated flag sets the task aside as NeedsReview (on-disk marker + in-memory) and the drain continues. The drain only halts when `ConsecutiveFlagThreshold` (3) consecutive flags occur with no Committed reset. `\"commit rejected:\"` flags retain their faster `CommitRejectThreshold` (2).\n\n## File-by-file changes\n\n### 1. `src/VisualRelay.Core/Queue/DrainCircuitBreaker.cs` (56→60 lines)\n- Add `private const int ConsecutiveFlagThreshold = 3;`\n- Add `private int _consecutiveFlags;`\n- Restructure `ShouldHalt`:\n  - All `Flagged` outcomes increment `_consecutiveFlags`\n  - `\"commit rejected:\"` sub-path increments `_consecutiveCommitRejects`; halts at ≥2\n  - Non-commit-reject flags reset `_consecutiveCommitRejects`; halt only at `_consecutiveFlags ≥ 3`\n  - `Committed` resets both counters to 0\n  - `WriteMarker` called only on actual halt returns\n\n### 2. `src/VisualRelay.Core/Queue/RelayQueueController.cs` (112→~122 lines)\n- In `DrainAsync`, after `ShouldHalt` returns false for a Flagged outcome:\n  - Write `NEEDS-REVIEW` file to `.relay/{taskId}/NEEDS-REVIEW` (so subsequent `RefreshAsync` picks up NeedsReview state)\n  - Re-add task to `Tasks` as `task with { ReviewReason = ... }` (so in-memory collection reflects it immediately)\n- At loop exit: `State = results.Any(r => r.Status == Flagged) ? ReviewNeeded : Completed`\n- Add private `WriteNeedsReviewMarker(string taskId, string reason)` helper\n\n### 3. `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs` (206→~212 lines)\n- Track `flaggedCount` during the drain loop\n- Continue past flagged outcomes when `ShouldHalt` returns false (was: implicit halt via return on every flag)\n- Final `StatusText`: `\"Queue drained · N flagged for review\"` when N>0, else unchanged\n\n### 4. `tests/VisualRelay.Tests/RelayQueueControllerTests.cs`\n- Add `ScriptedOutcomeTaskRunner` test double (~15 lines)\n- **Rewrite** `DrainAsync_HaltsAtFirstTaskThatNeedsReview` → `DrainAsync_ContinuesPastIsolatedFlag`:\n  3 tasks, middle Flagged → all 3 run; flagged one NeedsReview; State=ReviewNeeded; no DRAIN-HALTED\n- **New** `DrainAsync_ContinuesPastStalledTaskWithTimeoutReason`:\n  timeout-flagged task → skip+continue, not halt\n- **New** `DrainAsync_HaltsAtConsecutiveFlagThreshold`:\n  4 Flagged tasks → only first 3 run; DRAIN-HALTED written; 4th unrun\n- **New** `DrainAsync_CommittedBetweenFlagsResetsCounter`:\n  flag, commit, flag, commit, flag → all 5 run; no halt\n- **Keep** `DrainAsync_HaltsAfterRepeatedCommitGateRejections` — still passes (2 commit-rejects → halt)",
  "manifest": [
    "src/VisualRelay.Core/Queue/DrainCircuitBreaker.cs",
    "src/VisualRelay.Core/Queue/RelayQueueController.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs",
    "tests/VisualRelay.Tests/RelayQueueControllerTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/RelayQueueControllerTests.cs"],
  "rationale": "Added 4 new tests that encode the target behavior: (1) DrainAsync_ContinuesPastIsolatedFlag — 3 tasks, middle flagged → all 3 run, flagged task set aside as NeedsReview, no DRAIN-HALTED; (2) DrainAsync_ContinuesPastStalledTaskWithTimeoutReason — timeout flag treated like any other flag → skip+continue; (3) DrainAsync_HaltsAtConsecutiveFlagThreshold — 4 consecutive flags → first 3 run, drain halts at threshold, 4th un-run; (4) DrainAsync_CommittedBetweenFlagsResetsCounter — flag/commit/flag/commit/flag pattern → all 5 run, counter resets on each Committed. Added ScriptedOutcomeTaskRunner test double for scripting per-task outcomes. Rewrote DrainAsync_HaltsAtFirstTaskThatNeedsReview → DrainAsync_ContinuesPastIsolatedFlag. All 4 new tests FAIL before implementation (halt on first flag), 5 existing tests PASS."
}

## Stage 6 - Implement

{
  "summary": "## Changes Made\n\n### 1. `DrainCircuitBreaker.cs` — Generalized consecutive-flag circuit breaker\n- Added `ConsecutiveFlagThreshold = 3` constant and `_consecutiveFlags` counter\n- Restructured `ShouldHalt`:\n  - **commit-rejected** flags: increment both counters; halt at `CommitRejectThreshold` (2) or `ConsecutiveFlagThreshold` (3), whichever hits first\n  - **All other Flagged**: reset `_consecutiveCommitRejects`, increment `_consecutiveFlags`; halt only at ≥3 consecutive flags\n  - **Committed**: reset both counters to 0; never halt\n- `WriteMarker` only called on actual threshold-based halts (preserving `DRAIN-HALTED` semantics)\n\n### 2. `RelayQueueController.cs` — Continue past isolated flags, mark as NeedsReview\n- Moved the `ShouldHalt` check **after** the NeedsReview handling, so the task that triggers a halt is also set aside\n- On every Flagged outcome (including the halting one): write `.relay/{taskId}/NEEDS-REVIEW` file and re-add task to `Tasks` with `ReviewReason` set\n- After loop completes (no halt): set `State = ReviewNeeded` if any result was Flagged, otherwise `Completed`\n- Added private `WriteNeedsReviewMarker` helper\n\n### 3. `MainWindowViewModel.Execution.cs` — UI drain tracks flagged count\n- Tracks `flaggedCount` during the drain loop\n- On Flagged outcome (when circuit breaker continues): re-adds task to UI list as NeedsReview via `new TaskRowViewModel(task.Task with { ReviewReason = ... })`\n- Final `StatusText`: `\"Queue drained · N flagged for review\"` when N>0\n\n### 4. Tests — Updated for alphabetical ordering + new behavior\n- `DrainAsync_ContinuesPastIsolatedFlag`: 3 tasks, middle Flagged → all 3 run, flagged task set aside, no halt (PASSES)\n- `DrainAsync_ContinuesPastStalledTaskWithTimeoutReason`: timeout flag treated like any flag → skip+continue (PASSES)\n- `DrainAsync_HaltsAtConsecutiveFlagThreshold`: 4 flags, first 3 run, drain halts on 3rd, 4th un-run (PASSES)\n- `DrainAsync_CommittedBetweenFlagsResetsCounter`: flag/commit interleaved → all 5 run, counter resets (PASSES)\n- `DrainAsync_HaltsAfterRepeatedCommitGateRejections`: 2 commit-rejects still halt at threshold 2 (PASSES)\n- All 272 tests pass, 0 failures"
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Review stage 7 returned verdict 'pass' with zero issues. All 272 tests pass, build succeeds with 0 warnings and 0 errors. The implementation is complete: DrainCircuitBreaker generalizes consecutive-flag counting to all flag types with ConsecutiveFlagThreshold=3 (commit-rejects still trip at 2), RelayQueueController.DrainAsync marks flagged tasks NeedsReview and continues past isolated flags, and MainWindowViewModel.Execution.cs tracks flagged counts in the UI. No blockers or warnings remain." }

## Stage 9 - Verify

{
  "summary": "Generalized `DrainCircuitBreaker.ShouldHalt` to track consecutive flags of any type (not just commit-rejects) with `ConsecutiveFlagThreshold=3`, so isolated flagged/stalled tasks are set aside as `NeedsReview` and the drain continues to the next pending task. Only a burst of ≥3 consecutive flags (with no `Committed` reset) halts the drain and writes the `DRAIN-HALTED` marker. `RelayQueueController.DrainAsync` writes the on-disk `NEEDS-REVIEW` marker and re-adds the task in-memory with its review reason before checking the circuit breaker, ensuring flagged tasks survive a subsequent `RefreshAsync`. `MainWindowViewModel.Execution.cs` tracks a `flaggedCount` and reports `\"Queue drained · N flagged for review\"` when flags occurred. The commit-reject dual-threshold (2 consecutive) is preserved. 9 DrainAsync-specific tests and 272 total tests pass.",
  "commitMessages": [
    "feat: drain past isolated flagged/stalled tasks instead of halting on first flag",
    "Generalize DrainCircuitBreaker to all flag types with ConsecutiveFlagThreshold=3; set flagged tasks aside as NeedsReview and continue",
    "feat(queue): skip flagged/stalled tasks and continue draining — halt only on 3 consecutive flags"
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "Verified: dotnet test passes with 272/272 tests (0 failures). All 9 DrainAsync tests pass including the 4 new tests for continue-past-flag behavior. Build is clean with 0 warnings and 0 errors. The implementation generalizes DrainCircuitBreaker to track consecutive flags of any type with ConsecutiveFlagThreshold=3 (commit-rejects still trip at 2), DrainAsync marks flagged tasks NeedsReview and continues past isolated flags, and the UI tracks flagged counts. No failures to fix."
}

## Stage 11 - Commit

Committed by Visual Relay.

