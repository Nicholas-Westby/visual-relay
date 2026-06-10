## Stage 1 - Ideate

{
  "summary": "On resume, RelayDriver re-snapshots preRunUntracked, causing files authored by the interrupted instance to be classified as pre-existing and excluded from the sealed commit, breaking HEAD. Fix: persist the first-run snapshot across instances (Option A), add a post-commit untracked-file invariant check (Option C), and audit the manifest staging path for future hardening (Option B).",
  "options": [
    "Option A: Persist preRunUntracked to .relay/<taskId>/ at first instance start; load it on resume instead of re-snapshotting, so files born between first-start and resume-start are auto-include candidates again.",
    "Option B: Make GitCommitter.CommitAsync treat manifest entries (manifest.txt + amendManifest union) as authoritative commit content, staging them regardless of preRunUntracked classification.",
    "Option C: Add a post-commit invariant check — git status --porcelain must show no untracked files under source roots referenced by the commit's manifest — to catch the whole class of missing-file bugs early."
  ]
}

## Stage 2 - Research

{
  "findings": "The bug is at `RelayDriver.cs:157-161` where `preRunUntracked` is re-snapshotted at every run instance start (fresh or resume) via `GitCommitter.CaptureUntrackedSnapshotAsync`. The auto-include pass in `GitCommitter.CommitAsync:92-112` uses this snapshot to compute `currentUntracked \\ preRunUntracked` — files new since the run started. On a resumed instance, files authored by the interrupted instance (stages 5–10) are already untracked at resume-start → they appear in `preRunUntracked` → excluded from auto-include → missing from the sealed commit. The manifest-staging path (`ResolveManifestFilesToStageAsync`) only helps files listed in the stage-4 manifest; files created after stage 4 (new test files, new impl files the agent added during Implement/Review) rely solely on the auto-include pass. The `amendManifest` field in stage 10's JSON contract is declared but never read by the driver, so even agent-authored manifest amendments at fix-verify time are invisible. `LoadResumeState` loads ledger, seals, manifest, and status but has no concept of a persisted untracked snapshot. No post-commit invariant check exists to catch missing files.",
  "constraints": [
    "`.relay/<taskId>/` is the canonical persistence directory; any persisted preRunUntracked snapshot must live there (e.g., `pre-run-untracked.txt`).",
    "The persisted snapshot must only be loaded on `_options.Resume == true`; fresh runs snapshot as today.",
    "On a fresh (non-resume) run, any stale persisted snapshot must be replaced (or deleted) to avoid carrying forward a stale baseline.",
    "`ResolveManifestFilesToStageAsync` already correctly handles deleted manifest files (via `git ls-files --` fallback). This must not regress.",
    "`amendManifest` in stage 10 is an independent hardening path; requires reading from stage-10 JSON, unioning with stage-4 manifest, and updating `manifest.txt` on disk.",
    "Any post-commit invariant check must be cheap (a `git status --porcelain` scan, not a full build) and safe on any repo layout.",
    "Tests must use `TestRepository`, `FlagAtStageSubagentRunner`, and existing git helper patterns (`InitGitRepo` + `StageAndCommitSeed` for git-backed tests).",
    "The `EditingSubagentRunner` creates `src/status.cs` and `tests/status.test`; the `NewTestFileNotInManifestRunner` creates `tests/regression-tests.cs`. Resume-interruption tests should use `FlagAtStageSubagentRunner` wrapping these runners to simulate interruption after files are authored."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The bug is a two-part mechanism confirmed by code, git history, and run artifacts:\n\n1. **RelayDriver.cs:157-161** — `preRunUntracked` is re-snapshotted via `GitCommitter.CaptureUntrackedSnapshotAsync` at every run instance start, including resume. `LoadResumeState` (RelayDriver.VerifyFix.cs:17-70) loads ledger/seals/manifest/status but has no persistence for the untracked snapshot.\n\n2. **GitCommitter.cs:92-112** — The auto-include pass computes `currentUntracked \\ preRunUntracked` and only stages the delta. On a resumed instance, files authored by the interrupted instance (stages 5–10) are already untracked at resume-start → they fall into `preRunUntracked` → classified as pre-existing → excluded from the sealed commit.\n\n3. **Git commit db7d3c8** — The sealed commit for 'parallelize-planning-across-tasks' includes only 2 new files (those in the stage-4 manifest) out of 10 authored. Modified files like `RelayQueueController.cs` reference the missing classes (e.g. `DrainLifecycleCallbacks`).\n\n4. **Git commit 99646ae** — The follow-up explicitly adds the 10 missing files with message: 'The parallelize commit referenced these classes/tests but the resumed run's sealed commit excluded them (preRunUntracked misclassification on resume). HEAD did not build from a fresh checkout until this commit.'\n\n5. **Manifest gap** — Stage-4 manifest only listed 3 files. The 10 new files authored during Implement/Review/Fix/Verify relied solely on auto-include, which failed on resume.\n\n6. **Run artifacts** — The task directory shows extensive attempt retries (stages 1–3: 10–16 attempts, stages 5–8: 2–5 attempts), confirming multiple run instances with resume. The final status.json shows all Done but the commit is incomplete.\n\n7. **No test coverage** — `GitCommitterAutoIncludeTests` tests single-instance auto-include but never simulates interrupt→resume. `RelayDriverResumeTests` tests resume mechanics but never exercises git commit on resume.",
  "excerpts": [
    "RelayDriver.cs:157-161 — preRunUntracked re-snapshotted at every run instance start: var preRunUntracked = _options.CreateGitCommit ? await GitCommitter.CaptureUntrackedSnapshotAsync(rootPath, cancellationToken) : null;",
    "RelayDriver.VerifyFix.cs:17-70 — LoadResumeState loads ledger, seals, manifest, status. No preRunUntracked persistence. Only: LoadResumeState(taskDirectory, taskId, ledger, manifest, seals, ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount, statusEntries, ref firstStageToRun);",
    "GitCommitter.cs:92-112 — Auto-include computes delta: currentUntracked \\ preRunUntracked. On resume, interrupted-instance files are in preRunUntracked → excluded: if (!preRunUntracked.Contains(path) && !IsInternalArtifact(path)) { newAuthored.Add(path); }",
    "GitCommitter.cs:217-244 — CaptureUntrackedSnapshotAsync uses git ls-files --others --exclude-standard to capture all non-ignored untracked files. No filtering for run-authored vs pre-existing.",
    "Git commit db7d3c8 — sealed commit has only 2 new files (DrainExecutionLoggingTests.cs, RelayQueueControllerTwoPhaseTests.cs) which were in the stage-4 manifest. 10 authored files missing.",
    "Git commit 99646ae message — 'The parallelize commit referenced these classes/tests but the resumed run's sealed commit excluded them (preRunUntracked misclassification on resume — spec'd in resume-commit-omits-prior-authored-files). HEAD did not build from a fresh checkout until this commit.'",
    ".relay/parallelize-planning-across-tasks/status.json — All 11 stages Done. Seals chain complete. But manifest.txt only lists 3 files, far fewer than the 10+ files actually authored.",
    ".relay/parallelize-planning-across-tasks/ — Stage attempt directories show: stage1-attempt1 through stage1-attempt10 (10 iterations), stage2 up to attempt16, stage3 up to attempt12, stage4 up to attempt8, stage5 up to attempt5, stage6 up to attempt5, stage7 up to attempt2, stage8 up to attempt2, stage9 up to attempt2, stage10 up to attempt3. Multiple resume cycles confirmed."
  ],
  "repro": "1. Create a task that authors new files in stages 5–10 (outside the stage-4 manifest).\n2. Run with a subagent runner that flags at stage 11 (or use FlagAtStageSubagentRunner).\n3. Resume the task with Resume: true.\n4. Observe: the sealed commit contains only manifest-listed files and modified tracked files. New files authored by the first instance (stages 5–10) are untracked at resume start → classified as preRunUntracked → excluded from auto-include → missing from commit.\n\nConcrete repro using existing test infrastructure:\n- Reproduce with FlagAtStageSubagentRunner wrapping a runner that creates new files at stages 5-6 (like EditingSubagentRunner but with more new files), flag at stage 11.\n- Then run again with Resume: true and a happy-path runner.\n- Assert the commit's file list includes all files authored across both instances.\n- Current GitCommitterAutoIncludeTests.ExcludesPreExistingUntrackedFile passes because it only tests single-instance — the pre-existing file is a genuine operator scratch file, not a prior-instance-authored file."
}

## Stage 4 - Plan

{
  "plan": "## Fix: Persist pre-run untracked snapshot across run instances\n\n### Root cause\n`RelayDriver.RunTaskAsync` (line 157-161) re-snapshots `preRunUntracked` via `CaptureUntrackedSnapshotAsync` at every run instance start — including resume. On a resumed instance, files authored by the interrupted instance (stages 5–10) are already untracked → they appear in the new snapshot → classified as pre-existing → excluded from auto-include → missing from sealed commit.\n\n### Changes\n\n#### 1. RelayDriver.cs (lines 157-161) — Persist/load snapshot across instances\nReplace the simple inline snapshot with logic that:\n- On **non-resume** (fresh run): capture snapshot AND persist to `.relay/<taskId>/pre-run-untracked.txt` (overwrites any stale file).\n- On **resume**: load the persisted snapshot from that file instead of re-snapshotting. If the file is missing (legacy resume), fall back to fresh snapshot + persist.\n\nThis way files authored by the interrupted instance remain classified as \"new\" (absent from the FIRST instance's snapshot) and are auto-included on resume.\n\n#### 2. RelayDriver.Artifacts.cs — Add two static helpers\n- `WritePreRunUntrackedAsync(string path, IReadOnlySet<string> paths, CancellationToken ct)` — writes sorted paths, one per line.\n- `ReadPreRunUntrackedAsync(string path, CancellationToken ct)` — reads back as `HashSet<string>`.\n\n#### 3. GitCommitter.cs — Add `FindUncommittedAuthoredFilesAsync` post-commit invariant check\nA new public static method that captures `currentUntracked`, subtracts `preRunUntracked`, and subtracts internal artifacts (`.relay/`, `.swival/`, `.relay-scratch/`). Returns the leftover paths — any non-empty result means authored files were left behind. Used by the driver after commit succeeds.\n\n#### 4. RelayDriver.cs (after commit, line 402) — Post-commit invariant check\nAfter `CommitAsync` succeeds, call `FindUncommittedAuthoredFilesAsync`. If any paths are returned, rollback retirement and flag at stage 11 with a diagnostic message listing the missed files. This catches the entire class of missing-file bugs (not just resume).\n\n### Tests\n\n#### 5. GitCommitterAutoIncludeTests.cs — Two new unit tests\n- **(a) `CommitAsync_UsesFirstInstanceSnapshot_IncludesPriorInstanceFiles`**: Seed repo, snapshot=empty (first instance), author new files, commit with that snapshot → files included.\n- **(b) `CommitAsync_ExcludesPreExistingOperatorFile_WithFirstInstanceSnapshot`**: Pre-existing scratch file captured in first-instance snapshot stays excluded; newly authored files are included.\n\n#### 6. RelayDriverGitCommitTests.cs — One new integration test\n- **(c) `RunTaskAsync_Resume_CommitsFilesAuthoredBeforeInterruption`**: First run uses `FlagAtStageSubagentRunner(flagAtStage:9)` wrapping `NewTestFileNotInManifestRunner` (creates `tests/regression-tests.cs` at stage 5 — NOT in stage-4 manifest). First run flags at stage 9. Resume with `Resume:true` and a happy-path runner. Assert committed files include `tests/regression-tests.cs`.",
  "manifest": [
    "src/VisualRelay.Core/Execution/RelayDriver.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs",
    "src/VisualRelay.Core/Execution/GitCommitter.cs",
    "tests/VisualRelay.Tests/GitCommitterAutoIncludeTests.cs",
    "tests/VisualRelay.Tests/RelayDriverGitCommitTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/GitCommitterAutoIncludeTests.cs",
    "tests/VisualRelay.Tests/GitCommitterAutoIncludeTests.Snapshot.cs",
    "tests/VisualRelay.Tests/RelayDriverGitCommitTests.cs"
  ],
  "rationale": "Three author-tests added, one per spec target:\n\n(a) `CommitAsync_UsesFirstInstanceSnapshot_IncludesPriorInstanceFiles` — GitCommitter unit test: when given the *first*-instance snapshot (S1, empty) instead of the resume snapshot (S2, which already contains authored files), files authored between S1 and S2 are auto-included. PASSES with current GitCommitter (it already does the right thing when handed S1; the bug is that RelayDriver hands it S2).\n\n(b) `CommitAsync_ExcludesPreExistingOperatorFile_WithFirstInstanceSnapshot` — GitCommitter unit test: an operator scratch file captured in S1 remains excluded across resume; newly authored files are still included. PASSES with current GitCommitter.\n\n(c) `RunTaskAsync_Resume_CommitsFilesAuthoredBeforeInterruption` — integration test through RelayDriver: first run uses FlagAtStageSubagentRunner(flagAtStage:9) wrapping NewTestFileNotInManifestRunner (creates tests/regression-tests.cs at stage 5 — NOT in the stage-4 manifest), flags at stage 9. Resume with Resume:true and a happy-path runner. Result: committed files do NOT include tests/regression-tests.cs. FAILS — exactly the bug: the resume re-snapshots preRunUntracked, classifying the first-instance-authored file as pre-existing and excluding it."
}

## Stage 6 - Implement

{
  "summary": "## Changes implemented\n\n### Root cause\n`RelayDriver.RunTaskAsync` re-snapshotted `preRunUntracked` at every run instance start (including resume). On a resumed instance, files authored by the interrupted instance (stages 5–10) were already untracked → appeared in the new snapshot → classified as pre-existing → excluded from auto-include → missing from sealed commit. HEAD did not build from a fresh checkout.\n\n### Fix (4 production changes + 3 new tests)\n\n**1. RelayDriver.Artifacts.cs** — Added `WritePreRunUntrackedAsync` and `ReadPreRunUntrackedAsync` static helpers for persisting/loading the first-instance untracked snapshot to `.relay/<taskId>/pre-run-untracked.txt`.\n\n**2. RelayDriver.cs (lines 157-176)** — Replaced the inline snapshot with persist-on-first / load-on-resume logic. On a fresh run, captures the snapshot AND persists it. On resume, loads the persisted file instead of re-snapshotting (legacy resumes without the file fall back to capture+persist). Files authored by the interrupted instance are absent from the first snapshot → auto-included correctly.\n\n**3. GitCommitter.cs** — Added `FindUncommittedAuthoredFilesAsync` public static method: captures current untracked, subtracts `preRunUntracked`, subtracts internal artifacts, returns leftover paths. A post-commit invariant check that catches the whole class of missing-file bugs.\n\n**4. RelayDriver.cs (lines 419-433)** — Post-commit invariant check: after `CommitAsync` succeeds, calls `FindUncommittedAuthoredFilesAsync`. If any authored files were left behind, rolls back retirement and flags at stage 11 with a diagnostic listing the missed files.\n\n### Tests (all 23 pass)\n\n- `CommitAsync_UsesFirstInstanceSnapshot_IncludesPriorInstanceFiles` — unit test: S1 snapshot includes files authored between S1 and S2 (already authored in stage 5).\n- `CommitAsync_ExcludesPreExistingOperatorFile_WithFirstInstanceSnapshot` — unit test: operator scratch file in S1 stays excluded across resume (already authored in stage 5).\n- `RunTaskAsync_Resume_CommitsFilesAuthoredBeforeInterruption` — integration test through RelayDriver: FlagAtStageSubagentRunner wraps NewTestFileNotInManifestRunner, flags at stage 9, resume commits → `tests/regression-tests.cs` lands in the sealed commit. **Was FAILING, now PASSES.**\n- `FindUncommittedAuthoredFilesAsync_ReturnsEmptyWhenCommitIsComplete` — new: invariant returns empty when commit captured everything.\n- `FindUncommittedAuthoredFilesAsync_ReturnsMissedAuthoredFiles` — new: invariant returns missed files when auto-include skipped.\n- `FindUncommittedAuthoredFilesAsync_ExcludesInternalArtifacts` — new: internal artifacts (.relay/, .swival/) never reported as missed."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{
  "summary": "Resolved legacy-resume edge case in Stage 8: on resume without a persisted pre-run-untracked.txt (task interrupted before this fix was deployed), use an empty baseline instead of re-snapshotting, so prior-instance files are auto-included rather than silently dropped. All 498 tests pass (23/23 in the affected test classes). The fix is complete: fresh runs persist the first-instance snapshot to .relay/<taskId>/pre-run-untracked.txt; resumes load it; legacy resumes use empty; post-commit invariant catches any remaining missing-file bugs."
}

## Stage 9 - Verify

{
  "summary": "Fixed the resume-commit-omits-prior-authored-files bug where a resumed RelayDriver run re-snapshotted preRunUntracked, causing files authored by the interrupted instance to be classified as pre-existing and excluded from the sealed commit, breaking HEAD builds. The fix persists the first-instance untracked snapshot to `.relay/<taskId>/pre-run-untracked.txt` (fresh runs write it, resumes load it); a legacy-resume fallback uses an empty baseline so prior-instance files are auto-included. A post-commit invariant (`FindUncommittedAuthoredFilesAsync`) cheaply checks git status after every commit and flags at stage 11 if authored files remain untracked. Seven new tests cover the unit path (first-instance snapshot includes prior files while excluding operator scratch), the invariant check (empty when complete, reports when gap, excludes internal artifacts), and an end-to-end RelayDriver integration test (interrupted→resume→committed). All 498+ tests pass.",
  "commitMessages": [
    "fix(relay): persist first-instance untracked snapshot across resume to include prior-authored files",
    "fix: load preRunUntracked from persisted file on resume instead of re-snapshotting",
    "fix(relay): add post-commit invariant check that flags missing authored files after sealed commit",
    "fix: resume no longer drops files authored by interrupted run instance from sealed commit",
    "fix(relay): auto-include cross-instance authored files via persisted pre-run-untracked.txt"
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "## Stage 10 Fix-verify: All 498 tests pass (exit 0)\n\nThe implementation from stages 5–9 was complete and correct:\n- **RelayDriver.Artifacts.cs**: `WritePreRunUntrackedAsync` / `ReadPreRunUntrackedAsync` persist/load the first-instance untracked snapshot to `.relay/<taskId>/pre-run-untracked.txt`.\n- **RelayDriver.cs**: Fresh runs capture+persist the snapshot; resumes load it (legacy resumes without the file use empty baseline). Files authored by interrupted instances are absent from the first snapshot → auto-included correctly.\n- **GitCommitter.cs**: `FindUncommittedAuthoredFilesAsync` post-commit invariant check catches missing-file bugs.\n- **RelayDriver.cs (post-commit)**: Invariant check runs after commit; if authored files remain untracked, rolls back retirement and flags at stage 11.\n\n### New tests (10 total across the fix)\n- `CommitAsync_UsesFirstInstanceSnapshot_IncludesPriorInstanceFiles` — unit: S1 snapshot includes prior-instance files\n- `CommitAsync_ExcludesPreExistingOperatorFile_WithFirstInstanceSnapshot` — unit: operator scratch excluded, authored files included\n- `RunTaskAsync_Resume_CommitsFilesAuthoredBeforeInterruption` — integration: interrupt→resume→committed\n- `FindUncommittedAuthoredFilesAsync_ReturnsEmptyWhenCommitIsComplete` — invariant: empty when complete\n- `FindUncommittedAuthoredFilesAsync_ReturnsMissedAuthoredFiles` — invariant: reports gap\n- `FindUncommittedAuthoredFilesAsync_ExcludesInternalArtifacts` — invariant: excludes .relay/.swival\n- Plus pre-existing snapshot+auto-include tests (7 more)\n\n### Test suite stability fix\nAdded `[Collection(\"GitCommitter\")]` to 6 test classes that use the static `GitCommitter.RawGitRunner` field (`GitCommitterTests`, `GitCommitterAutoIncludeTests`, `RelayDriverGitCommitTests`, `RelayDriverGitCommitRetirementTests`, `RelayDriverManifestScopeTests`, `NoCommitContaminationTests`). This serializes their execution, eliminating intermittent \"not a git repository\" failures caused by parallel tests leaking the static shim across classes."
}

## Stage 11 - Commit

Committed by Visual Relay.

