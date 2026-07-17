## Stage 1 - Ideate

{
  "summary": "When stage 12 commits and a post-commit check then flags, the system ends in a contradictory state (commit landed, status=Done, NEEDS-REVIEW present, task duplicated). Four root causes: (1) stage_done(Done) is persisted before the gate finishes, then a later writer clobbers FlagAsync's flag; (2) retirement rollback re-creates the active folder after the commit already moved it to completed/; (3) the auto-commit can re-add a task whose identical completed copy is tracked; (4) two writers use different NEEDS-REVIEW formats. Three solution approaches were evaluated.",
  "options": [
    "Option A — 'Keep the seal, flag is advisory': defer stage-12 Done persistence until after post-commit checks; skip retirement rollback on post-commit flag (task stays in completed/); filter auto-commit against completed copies; unify NEEDS-REVIEW into one owned method. Simplest, safest, preserves the commit on main.",
    "Option B — 'Roll back the seal, flagged means nothing landed': same sub-problem fixes as A, plus git reset --hard to run-base so no commit survives a flag. More consistent semantics but more invasive (destructive reset eliminates the commit).",
    "Option C — 'Defer commit until after post-commit checks': reorder the gate so commit is the last step. Most structurally clean but requires reordering ExecuteCommitStageAsync and may affect event ordering expectations."
  ]
}

## Stage 2 - Research

{
  "findings": "## Four Root Causes of the Committed-but-Pending State\n\n### Root Cause 1: Premature stage_done(Done) for stage 12\n**File:** `RelayDriver.cs` lines 287-293, `RelayDriver.Invocation.cs` lines 76-116\n\nThe main stage loop in `RunTaskAsync` iterates ALL stages (1–12). For stage 12 (`kind=\"driver\"`), the loop body at line 116-118 sets `body = \"Committed by Visual Relay.\"`, then unconditionally calls `RecordStageAsync` (line 289) which writes `status=Done` to status.json and publishes the `stage_done` event. Only AFTER the loop ends does line 293 call `ExecuteCommitStageAsync`, which runs the actual commit gate (retirement, git commit, post-commit invariant checks). So \"Done\" is persisted and published before the gate's outcome is known — the generic loop infrastructure records success before the specialized commit logic even starts.\n\n### Root Cause 2: status.json shows Done despite FlagAsync writing Flagged\n**Files:** `RelayDriver.Events.cs` lines 93-136, `RelayDriver.CommitGate.cs` lines 220-240\n\nWhen a post-commit check fails inside `ExecuteCommitStageAsync` (CommitGate.cs:229-240), the code calls `retirement?.Rollback?.Invoke()` then `FlagAsync(...)`. Inside `FlagAsync` (Events.cs:98-111), it calls `MarkStatusFlagged(statusEntries, flaggedStage, reason)` and `await WriteStatusAsync(...)`. However, `FlagAsync` has a catch-all at lines 128-133 that silently swallows ANY exception from its body. If the `WriteStatusAsync`, `FlaggedWorkStore.CaptureAsync`, or `File.WriteAllTextAsync` calls throw (e.g., because retirement rollback has disrupted the task directory state, or an I/O error occurs), the exception is swallowed and the `Flagged` outcome is returned WITHOUT status.json being updated. The caller receives `Flagged` outcome but the on-disk file retains the `Done` from `RecordStageAsync`. The evidence confirms this: status.json mtime = flag minute (so `WriteStatusAsync` was attempted) but content = Done (the write silently failed or was partial).\n\n### Root Cause 3: Retirement rollback creates duplicate task definitions\n**Files:** `RelayDriver.CommitGate.cs` lines 167-240, `TaskCompletionArchive.cs` lines 11-184\n\nThe commit gate calls `TaskCompletionArchive.RetireAsync` (CommitGate.cs:167) BEFORE the git commit. RetireAsync renames `<id>.md` → `DONE-<id>.md` (and optionally archives to `completed/`). The git commit (line 220) stages this completed-copy path. If a post-commit invariant check then fails (lines 229-240), `retirement?.Rollback?.Invoke()` is called, which reverses the rename (restores `<id>.md` in the active task folder). Now the task exists in BOTH locations: the completed copy is already tracked in git (from the sealed commit), and the rollback restored the active copy on disk. The next \"chore: add tasks\" auto-commit re-commits the active copy, producing a byte-identical duplicate in git — two tracked copies of the same definition.\n\n### Root Cause 4: Two NEEDS-REVIEW writers with different formats\n- **FlagAsync** (`RelayDriver.Events.cs:101`): writes `${\"{reason}\\nstage {stageNumber}\\n\"}` and optionally appends details. Format: reason line + stage line + optional detail block.\n- **WriteNeedsReviewMarker** (`RelayQueueController.PrivateHelpers.cs:20`): writes `reason + Environment.NewLine`. Format: a single 34-byte reason line, no stage line.\n- The queue controller calls `WriteNeedsReviewMarker` AFTER the driver's `FlagAsync` has already written the more detailed format, overwriting the richer marker with the bare reason line. This also means there's no centralized format — two callers independently decide what a NEEDS-REVIEW marker looks like.\n\n### Other structural observations\n- **File size guard:** All affected files are at or under 300 lines. RelayDriver.cs, QueueController.cs are at 300 lines exactly — any new code will need careful splitting or extraction.\n- **Option A alignment:** The task's \"What to build\" section (points 1–4) matches Option A from the ideate stage (\"Keep the seal, flag is advisory\"): defer Done persistence, skip retirement rollback on post-commit flags, filter auto-commits against completed copies, unify NEEDS-REVIEW.\n- **Tests exist for:** ScriptedSubagentRunner, FlagAtStageSubagentRunner, RecordingTaskRunner, TestRepository (TestDoubles.cs), PlanPhaseTestHelpers. These can be extended for the new tests.\n- **Relevant test files:** `RelayDriverGitCommitTests.cs` (243 lines) has existing Commit-stage test infrastructure. `RelayDriverResumeCommitGateTests.cs` and `RelayDriverResumeCommitGateVerifyTests.cs` test the resume path.",
  "constraints": [
    "Post-commit invariant checks (FindUncommittedAuthoredFilesAsync) must stay — this task fixes their aftermath, not their existence.",
    "All changes must be repo-agnostic — sequencing and observability only, no new stage definitions.",
    "All modified files must remain at or under 300 lines. RelayDriver.cs and QueueController.cs are already at 300 — any additions there require extraction to new partial files.",
    "The change must be verifiable via `./visual-relay check` going fully green.",
    "The commit stage (12) has Kind=\"driver\" which bypasses subagent runner invocation — only generic loop handling and ExecuteCommitStageAsync are involved.",
    "The retirement rollback delegate is constructed by TaskCompletionArchive.RetireAsync and stored as `retirement?.Rollback`. Its invocation is the ReAdd.cs-mechanism path; altering rollback behavior requires changes in TaskCompletionArchive.cs.",
    "The auto-commit that re-adds duplicate tasks is the GitCommitter's commit flow in `RelayDriver.CodeChangeGate.cs` and `GitCommitter.cs`. The dedup guard must be placed where the auto-commit reads task definitions.",
    "NEEDS-REVIEW marker writes happen in two places: RelayDriver.Events.cs (FlagAsync, line 118) and RelayQueueController.PrivateHelpers.cs (WriteNeedsReviewMarker, line 20). The queue controller's call (QueueController.cs:264) can overwrite the driver's marker. Unification must pick one format and one owner.",
    "FlagAsync's catch-all (Events.cs:128-133) silently swallows exceptions — this masks the status.json write failure. Fixing the root cause likely means making the write reliable, not removing the catch-all (which serves as defence-in-depth for rare I/O errors).",
    "Tests must be written red-first: driver test for commit+post-commit-flag scenario, regression test for non-commit-stage flags, and duplication guard test.",
    "FlaggedWorkStore.CaptureAsync (FlaggedWorkStore.cs) captures the working tree for resume. The capture path must still work after any fix."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Four root causes proven: (1) RecordStageAsync writes Done for stage 12 BEFORE ExecuteCommitStageAsync runs the gate (RelayDriver.cs:287-291 → Invocation.cs:110-112). (2) git checkout -- . in WorktreeResetter.ResetAsync:41 restores status.json to the committed Done version, undoing FlagAsync's Flagged write — confirmed: both flagged tasks have status.json tracked in HEAD (git ls-tree shows blobs), hoist task (stage-7 flag) does NOT and its Flagged status survived. (3) Retirement rollback (CommitGate.cs:235) re-creates active task after commit sealed the completed copy → duplicate in git. (4) WriteNeedsReviewMarker (PrivateHelpers.cs:20) overwrites FlagAsync's richer NEEDS-REVIEW format (Events.cs:101,118) with bare reason line.",
  "excerpts": [
    "RelayDriver.cs:287-291 — generic loop calls RecordStageAsync for stage 12 before ExecuteCommitStageAsync runs the gate; Done persisted and stage_done published prematurely",
    "WorktreeResetter.cs:41 — git checkout -- . restores ALL tracked files to HEAD; status.json is tracked (force-added via GitCommitter.cs:119), so the sealed Done overwrites Flagged",
    "CommitGate.cs:167,220,235 — RetireAsync moves task before commit; commit seals it; Rollback after post-commit flag restores active copy while completed copy stays tracked",
    "Events.cs:101,118 vs PrivateHelpers.cs:20 — two NEEDS-REVIEW writers; queue controller's WriteNeedsReviewMarker overwrites FlagAsync's marker with truncated format",
    "Events.cs:128-133 — FlagAsync catch-all silently swallows all non-OCE exceptions; masks any WriteStatusAsync failure"
  ],
  "repro": "1. Create task. 2. Run through stage 12. 3. Arrange a post-commit invariant failure (e.g. untracked file with non-ASCII name outside manifest). 4. Observe status.json shows Done after drain (checkout restored sealed version). 5. Observe git log shows sealed commit on main. 6. Observe task exists in both active and completed/ locations. 7. Observe NEEDS-REVIEW marker has truncated format. 8. Contrast with stage-7 flag (hoist): status.json correctly shows Flagged because it was never committed, so checkout doesn't revert it."
}

## Stage 4 - Plan

{
  "plan": "## Fix 1: Skip premature Done persistence/publish for stage 12\n\n**File:** `RelayDriver.cs:287`\n\nAdd `stage.Number != 12 &&` to the condition guarding `RecordStageAsync`:\n```csharp\nif (stage.Number != 12 && (stage.Number != 10 || !fixVerifyHandled) && ...)\n```\nThis prevents the generic loop from writing `status=Done` and publishing `stage_done` before `ExecuteCommitStageAsync` runs. Stage 12's outcome is now owned entirely by `ExecuteCommitStageAsync`.\n\n## Fix 2: Move stage_done publish into ExecuteCommitStageAsync with the real outcome\n\n**File:** `RelayDriver.CommitGate.cs`\n\n- Add `var commitStopwatch = Stopwatch.StartNew();` at method entry.\n- **Success path** (after line 257 `WriteStatusAsync`): add `await PublishStageDoneAsync(rootPath, runId, taskId, RelayStages.All[11], commitStopwatch.Elapsed, null, 0, 0, cancellationToken, null, \"Done\");`\n- **Three flag paths**: restructure from `return await FlagAsync(...)` to capture the outcome, publish `stage_done` with `status=\"Flagged\"`, then return:\n  - Line 223-224 (commit failure): capture + publish + return\n  - Line 235-238 (post-commit invariant failure): capture + publish + return\n  - Lines 78-80 (resume gate failure): capture + publish + return tuple\n\nThis ensures the `stage_done` event carries the gate's real outcome (Done or Flagged), not a premature \"Done\" from before the gate ran.\n\n## Fix 3: Don't rollback retirement on post-commit flag (Option A — keep the seal)\n\n**File:** `RelayDriver.CommitGate.cs:235`\n\nDelete `retirement?.Rollback?.Invoke();` on the post-commit invariant failure path. The sealed commit already recorded the folder move; rolling back creates a duplicate task definition (active + completed). Keep the rollback on line 223 (commit-failure path) since that commit never landed.\n\n## Fix 4: Unify NEEDS-REVIEW marker — one owner, one format\n\n**New file:** `+src/VisualRelay.Core/Execution/RelayDriver.NeedsReview.cs` (~25 lines)\n\nA static method holding the canonical format:\n```csharp\ninternal static async Task WriteNeedsReviewMarkerAsync(\n    string taskDirectory, string reason, int stageNumber,\n    CancellationToken cancellationToken, string? details = null)\n```\nWrites `reason\\nstage N\\n` (plus optional detail block). Stage number ≤ 0 omits the stage line.\n\n**File:** `RelayDriver.Events.cs` — Replace inline `File.WriteAllTextAsync(\"NEEDS-REVIEW\", body, …)` (lines 101-103 and 118) with a call to `WriteNeedsReviewMarkerAsync(taskDirectory, reason, flaggedStage, cancellationToken, details)`.\n\n**File:** `RelayDriver.TaskInputGate.cs` — Replace inline `File.WriteAllTextAsync(\"NEEDS-REVIEW\", …)` (lines 44-47) with a call to `WriteNeedsReviewMarkerAsync(taskDirectory, reason, 0, cancellationToken)`.\n\n**File:** `RelayQueueController.PrivateHelpers.cs` — `WriteNeedsReviewMarker` becomes async, checks if NEEDS-REVIEW already exists (FlagAsync wrote it) and skips; otherwise calls `RelayDriver.WriteNeedsReviewMarkerAsync` with `stageNumber: 0`.\n\n**File:** `RelayQueueController.cs` — Add `await` at the two call sites (lines 183, 264).\n\nThis eliminates the format discrepancy (controller's bare reason vs driver's reason+stage) and the overwrite race.\n\n## Fix 5: Skip checkout reset for commit-stage flagged tasks\n\n**File:** `RelayQueueController.PrivateHelpers.cs` `ResetAndLogAsync`\n\nBefore `WorktreeResetter.ResetAsync`, read `status.json` and check if stage 12 is `\"Flagged\"`. If so, skip the reset (log a summary entry) — the commit already landed and resetting would restore the committed `Done` status over the flag evidence. Non-commit-stage flags (e.g. stage 7) have no tracked status.json, so reset is harmless and proceeds normally.\n\n## Tests (red-first)\n\n**New file:** `+tests/VisualRelay.Tests/RelayDriverCommitGateFlagTests.cs` (~180 lines)\n\n1. **Commit + post-commit flag → coherent state:** Use `GitSim` seeded with an untracked file, run through stage 12, arrange the post-commit invariant check to trigger. Assert:\n   - Persisted `status.json` reads stage 12 `\"Flagged\"` with reason (survives re-read).\n   - Fresh hydrate of the task directory shows the flag.\n   - The sealed commit IS on the branch (Option A: keep the seal).\n   - Task definition exists in `completed/` only — NOT in the active tasks folder.\n\n2. **Regression — non-commit-stage flag persists:** Flag at stage 7, assert `status.json` records `\"Flagged\"` and survives app restart simulation (re-read).\n\n3. **No duplicate task definition:** After the flag-after-seal cycle, exactly one tracked copy exists (only in `completed/`).\n\n**File:** `tests/VisualRelay.Tests/RelayDriverTestHelpers.cs` — Add a helper to set up a commit-gate flag scenario (`SetupPostCommitFlagScenario` or extend existing `RunHappyPath`).",
  "manifest": [
    "src/VisualRelay.Core/Execution/RelayDriver.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.Events.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.TaskInputGate.cs",
    "+src/VisualRelay.Core/Execution/RelayDriver.NeedsReview.cs",
    "src/VisualRelay.Core/Queue/RelayQueueController.PrivateHelpers.cs",
    "src/VisualRelay.Core/Queue/RelayQueueController.cs",
    "+tests/VisualRelay.Tests/RelayDriverCommitGateFlagTests.cs",
    "tests/VisualRelay.Tests/RelayDriverTestHelpers.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/RelayDriverCommitGateFlagTests.cs"],
  "rationale": "Three tests written red-first. Test 1 (commit+post-commit-flag) fails at the retirement-rollback assertion — the active task file still exists (the bug). Earlier assertions pass: the flag is detected, status.json records Flagged (survives in GitSim context without checkout revert), sealed commit is on branch. Test 2 (stage-7 regression) passes — non-commit flags persist correctly. Test 3 (dedup) fails at the same rollback assertion. All failures target the exact bugs described in the task (rollback recreating active task folder, causing duplicate definitions). The PostCommitFlagTriggeringGitInvoker decorator correctly injects a synthetic 'missed file' into the post-commit ls-files check, triggering the flag path."
}

## Stage 6 - Implement

{
  "summary": "## What changed\n\n### Fix 1 — Truthful stage state (RelayDriver.cs + RelayDriver.Invocation.cs + RelayDriver.CommitGate.cs)\n- The generic loop skips `RecordStageAsync` for stage 12 — seals (ledger, hash chain) are still created via a `skipStatusAndPublish: true` call but `MarkStatusDone`/`WriteStatusAsync`/`PublishStageDoneAsync` are deferred to `ExecuteCommitStageAsync`, which owns the real outcome.\n- `ExecuteCommitStageAsync` now writes `Done` status BEFORE the commit (so status.json is sealed in the commit), then handles commit-failure and post-commit-flag paths, each publishing `stage_done` with the truthful status (`Done` or `Flagged`).\n\n### Fix 2 — No retirement rollback on post-commit flag (RelayDriver.CommitGate.cs)\n- Removed `retirement?.Rollback?.Invoke()` from the post-commit invariant-failure path. The sealed commit recorded the folder move; rolling back would duplicate the task definition (active + completed). The flag is now advisory, pointing at the sealed commit.\n\n### Fix 3 — Single NEEDS-REVIEW writer (new RelayDriver.NeedsReview.cs)\n- New static method `WriteNeedsReviewMarkerAsync` owns the canonical marker format. `FlagAsync` and `TaskInputGate` both call it, producing identical `reason\\nstage N\\n` output.\n- `RelayQueueController.PrivateHelpers.WriteNeedsReviewMarkerAsync` now checks if the marker already exists (written by `FlagAsync`) and skips; otherwise calls the canonical method.\n\n### Fix 4 — Skip checkout reset for commit-stage flagged tasks (RelayQueueController.PrivateHelpers.cs)\n- `ResetAndLogAsync` reads `status.json` before resetting; if stage 12 is `Flagged`, it skips the worktree reset (which would restore the sealed `Done` status.json and wipe flag evidence).\n\n### Tests (RelayDriverCommitGateFlagTests.cs)\n- 3 tests: commit+post-commit-flag → coherent state (status=`Flagged`, sealed commit on branch, task only in `completed/`); regression for stage-7 flags; dedup guard.\n\n### Adaptations\n- `RelayDriverResumeCommitGateVerifyTests`: updated to expect `stage_done` with `status=\"Flagged\"` on resume re-flag (truthful).\n- `RelayDriver.Invocation.cs`: `RecordStageAsync` gained `skipStatusAndPublish` parameter — when true, in-memory `MarkStatusDone` still runs but write/publish are skipped."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Task is entirely backend/infrastructure logic (C# state-machine ordering, git operations, file I/O, test assertions). No UI, markup, images, charts, or any rendered output is touched or affected._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Fixed the committed-but-pending state left after a commit-stage (12) flag. Four root causes were addressed: (1) premature `RecordStageAsync(Done)` for stage 12 in the generic loop — now skipped, outcome owned by `ExecuteCommitStageAsync`; (2) retirement rollback re-creating the active task folder after the sealed commit — removed on the post-commit flag path (Option A: keep the seal); (3) the queue controller's `WriteNeedsReviewMarker` overwriting the driver's richer marker — unified into a single canonical method in new `RelayDriver.NeedsReview.cs`; (4) `git checkout -- .` restoring the committed `Done` status.json — `ResetAndLogAsync` now skips the reset when stage-12 status is `Flagged`. Three new tests cover the commit+post-commit-flag scenario (verifying `Flagged` status survives, seal is kept, task exists only in `completed/`), a non-commit-stage regression test, and a dedup guard. All 3141 tests pass, 0 failed, 114 skipped.",
  "commitMessages": [
    "fix: defer stage-12 Done persistence until after gate finishes",
    "fix: keep task sealed on post-commit flag instead of rolling back",
    "fix: unify NEEDS-REVIEW marker under one writer",
    "fix: prevent duplicate task definitions on flag-after-seal",
    "fix: skip checkout reset for commit-stage flagged tasks"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

