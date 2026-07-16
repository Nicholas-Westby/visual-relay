## Stage 1 - Ideate

{
  "summary": "Fix CapturePreRunUntrackedAsync to capture+persist when snapshot is missing on resume (mirroring CaptureRunBaseShaAsync), make WorktreeResetter.ResetAsync truthful about actual deletions, and refuse to delete anything when the baseline snapshot is absent — with three test scenarios covering capture on fresh resume, reuse on existing snapshot, and resetter honesty on failed deletes.",
  "options": [
    "Option A — Minimal fix + defensive reset guard: replace empty-set fallback with fresh capture+persist, track actual File.Delete successes in the resetter, and refuse deletion when snapshot is absent. Preserves forceFresh as-is.",
    "Option B — Full symmetry + resetter audit + forceFresh elimination: same snapshot+resetter fix as Option A, plus explicit documentation/audit of forceFresh rationale. Refuse deletion at reset with a loud structured error event.",
    "Option C — Conservative heuristic fallback at reset: same snapshot+resetter fix, but when snapshot is missing, delete only files proven safe (outside .relay/<task>/) rather than refusing entirely. More complex but graceful."
  ]
}

## Stage 2 - Research

{
  "findings": "The bug is in `CapturePreRunUntrackedAsync` (RelayDriver.Snapshot.cs:56-63): when `Resume=true`, `forceFresh=false`, and `pre-run-untracked.txt` does not exist, the method returns `new HashSet<string>()` without writing the snapshot file — an empty baseline that makes every pre-existing untracked file appear authored by the run. `CaptureRunBaseShaAsync` (same file, lines 83-109) handles the identical scenario correctly by falling through to capture+persist. The consequence propagates to `WorktreeResetter.ResetAsync` (WorktreeResetter.cs:30-33) where the missing snapshot becomes an empty set, causing ALL untracked non-internal files to be deleted during a post-flag reset. Additionally, `ResetAsync` returns files it intended to delete (line 44) regardless of whether `File.Delete` actually ran (lines 47-52), inflating the drain log at `RelayQueueController.PrivateHelpers.cs:32`. The existing test `ResetAsync_MissingPreRunSnapshot_FallsBackToEmptySet` (WorktreeResetterTests.cs:141-155) enshrines this dangerous default and must be reversed. `forceFresh` (passed `isReAdded` from `RelayDriver.cs:69`) remains meaningful after the fix: it skips any existing persisted snapshot to force a fresh capture — same semantics as `CaptureRunBaseShaAsync` uses it.",
  "constraints": [
    "300-line guard: RelayDriver.Snapshot.cs (154), WorktreeResetter.cs (128), RelayQueueController.PrivateHelpers.cs (130) each have room for changes",
    "Repo-agnostic — no assumptions about untracked file directories (WorktreeResetter already satisfies this)",
    "Commit gate must not be weakened — the gate at RelayDriver.CommitGate.cs:216-227 compares against `preRunUntracked` and must keep doing so correctly",
    "`CapturePreRunUntrackedAsync` is private and tested only indirectly through `RelayDriver.RunTaskAsync` — new tests may need to exercise the driver with `Resume: true`, `CreateGitCommit: true` and a task dir that has `status.json` but no `pre-run-untracked.txt`",
    "`ResetAsync` return type is `IReadOnlyList<string>` — changing the return shape requires updating `ResetAndLogAsync` caller; consider a richer result record or separate out-parameter for failures",
    "`DrainSummaryLog.Write` is the established mechanism for drain-level events — use milestone values like `reset-remove-failed` or `reset-refused` for the distinct loud event",
    "`WorktreeResetter` is a static class with no constructor injection — it cannot sink events directly; the caller `ResetAndLogAsync` must translate failures into log events"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The bug is in CapturePreRunUntrackedAsync at RelayDriver.Snapshot.cs:60-63. When Resume=true, forceFresh=false, and pre-run-untracked.txt does not exist, the method returns new HashSet<string>() — an empty baseline — without writing the snapshot file. This fires on every first-ever GUI-driven run because GuiTaskRunner.cs:26 always sets Resume:true. CaptureRunBaseShaAsync at lines 83-109 handles the identical scenario correctly: when the persisted file is missing on resume, it falls through to capture+persist rather than returning a fabricated value. The empty baseline causes: (1) FindUncommittedAuthoredFilesAsync at CommitGate.cs:218-219 treats every pre-existing untracked file as authored, producing false flags; (2) WorktreeResetter.ResetAsync at lines 30-33 falls back to an empty set on the missing snapshot, then deletes every untracked non-internal file at lines 47-52 — a data-loss hazard; (3) ResetAsync returns its intent list (toDelete) rather than verified deletions, causing ResetAndLogAsync at PrivateHelpers.cs:28-34 to log reset-removed counts for files never actually deleted. The existing test ResetAsync_MissingPreRunSnapshot_FallsBackToEmptySet (WorktreeResetterTests.cs:141-155) enshrines this dangerous behavior as 'conservative' and must be reversed.",
  "excerpts": [
    "RelayDriver.Snapshot.cs:56-63 — the empty-set fallback on resume when snapshot file is missing",
    "RelayDriver.Snapshot.cs:83-109 — CaptureRunBaseShaAsync correctly falls through to capture+persist when file is missing, the pattern to mirror",
    "WorktreeResetter.cs:30-33 — missing snapshot → empty set, the dangerous default that cascades to delete everything",
    "WorktreeResetter.cs:47-52 — File.Delete without tracking success, and line 66 returns toDelete (intent) not actual results",
    "RelayQueueController.PrivateHelpers.cs:28-34 — ResetAndLogAsync logs reset-removed from the intent list, unaware of deletion failures",
    "WorktreeResetterTests.cs:141-155 — test that asserts missing snapshot → delete everything, must be reversed",
    "GuiTaskRunner.cs:26 — Resume:true on every GUI-driven run, making the empty-set branch the common path",
    "RelayDriver.CommitGate.cs:216-227 — commit gate uses preRunUntracked (empty set) to find 'missed' authored files, producing false flags",
    "GitCommitter.Untracked.cs:71-91 — FindUncommittedAuthoredFilesAsync subtracts baseline from current untracked; empty baseline means every file is 'authored'"
  ],
  "repro": "1. Create a git repo with a tracked file committed and an untracked file sitting on disk (e.g. 'scratch.log'). 2. Run any task through GuiTaskRunner (or any driver with Resume:true, CreateGitCommit:true). 3. Observe: pre-run-untracked.txt is never written to .relay/<taskId>/. 4. The commit gate at stage 12 flags 'scratch.log' as a missing authored file. 5. If the run is flagged, ResetAndLogAsync deletes 'scratch.log' and logs it as reset-removed even if deletion fails."
}

## Stage 4 - Plan

{
  "plan": "Fix CapturePreRunUntrackedAsync to capture+persist when snapshot is missing on resume (removing the empty-set fallback at lines 60-63), mirroring CaptureRunBaseShaAsync. Make WorktreeResetter.ResetAsync return a WorktreeResetResult record with Removed, Failed, and SnapshotMissing; refuse deletion when snapshot is absent; track actual File.Delete successes. Update ResetAndLogAsync to emit reset-refused and reset-remove-failed drain-log events. Reverse the dangerous ResetAsync_MissingPreRunSnapshot_FallsBackToEmptySet test; add resetter honesty test; add driver-level test verifying pre-run-untracked.txt is created on first resume-mode run.",
  "manifest": [
    "src/VisualRelay.Core/Execution/RelayDriver.Snapshot.cs",
    "src/VisualRelay.Core/Execution/WorktreeResetter.cs",
    "src/VisualRelay.Core/Queue/RelayQueueController.PrivateHelpers.cs",
    "tests/VisualRelay.Tests/WorktreeResetterTests.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 3.

> **Manifest merge (stage 5)**: added 1 authored test file(s).

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/RelayDriverResumeTests.cs",
    "tests/VisualRelay.Tests/WorktreeResetterTests.cs",
    "tests/VisualRelay.Tests/WorktreeResetterNonAsciiTests.cs"
  ],
  "rationale": "Two new driver-level tests exercise the fixed CapturePreRunUntrackedAsync (baseline capture + persist on first resume, reuse on existing snapshot). Three WorktreeResetter tests cover the safe-default refusal on missing snapshot, resetter honesty on phantom files, and updated callers for the new WorktreeResetResult record type. All 18 tests pass with the production fixes in place."
}

## Stage 6 - Implement

{ "summary": "Production changes: (1) RelayDriver.Snapshot.cs: removed the empty-set fallback from CapturePreRunUntrackedAsync — now falls through to capture+persist when snapshot file is missing on resume, mirroring CaptureRunBaseShaAsync. (2) WorktreeResetter.cs: added WorktreeResetResult record (Removed, Failed, SnapshotMissing); ResetAsync returns early with SnapshotMissing=true when pre-run-untracked.txt is absent; tracks actual File.Delete successes; failed deletes go to Failed not Removed. (3) RelayQueueController.PrivateHelpers.cs: ResetAndLogAsync emits reset-refused and reset-remove-failed drain-log events. Tests: reversed missing-snapshot test to refuse deletions; extracted honesty test to WorktreeResetterHonestyTests.cs (keeps WorktreeResetterTests under 300 lines); two new RelayDriverResumeTests for baseline capture+persist and snapshot reuse; bumped FactCount baseline 174→176. Full suite: 3229 pass, 0 fail." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: The task changes only backend C# logic: snapshot capture behavior, file deletion tracking, and drain-log events. No UI, layout, styling, images, charts, or documents are produced or modified._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Fix CapturePreRunUntrackedAsync to capture+persist fresh when snapshot missing on resume (instead of returning empty set), add WorktreeResetResult record so WorktreeResetter.ResetAsync returns only actually-deleted files and refuses deletion when pre-run-untracked.txt is absent, and emit reset-refused / reset-remove-failed drain-log events from ResetAndLogAsync. All 3115 tests pass (0 failed, 114 skipped).",
  "commitMessages": [
    "fix: capture+persist untracked baseline on resume when snapshot missing\n\n- Replace empty-set fallback in CapturePreRunUntrackedAsync with fresh\n  capture+persist when pre-run-untracked.txt is absent on resume\n- Refuse deletion entirely when snapshot file is missing at reset time\n- Track actual File.Delete successes; report failures separately\n- Emit reset-refused and reset-remove-failed drain-log events",
    "fix: mirror CaptureRunBaseShaAsync fallthrough for missing untracked snapshot\n\n- When Resume=true and pre-run-untracked.txt does not exist, capture and\n  persist the current untracked set instead of returning an empty baseline\n- Add WorktreeResetResult (Removed, Failed, SnapshotMissing) for honest\n  deletion accounting\n- Gate: no deletion when snapshot is absent",
    "fix: prevent data-loss hazard on missing pre-run-untracked.txt\n\nMake WorktreeResetter.ResetAsync return only files it actually deletes;\nrefuse to delete anything when the baseline snapshot file is missing.\nPreviously an empty-set fallback caused every pre-existing untracked file\nto be misattributed as authored and potentially deleted on flag.",
    "fix: honest deletion reporting in WorktreeResetter\n\n- ResetAsync now returns a WorktreeResetResult with distinct Removed/Failed/SnapshotMissing fields\n- Caller emits distinct drain-log events for refused and failed deletions\n- Reversed the dangerous test that asserted missing snapshot → delete everything"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

