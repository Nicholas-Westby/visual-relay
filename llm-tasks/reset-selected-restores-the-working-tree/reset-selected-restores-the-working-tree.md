# reset-selected puts the working tree back where the flagged run found it

Reset promises a flagged task a fresh start: the run directory is archived,
the task lists as Pending, the next run begins at stage 1. It says nothing
about the working tree, and the tree is where the flagged run's edits live.
Two exits from a flagged run reset the tree to the run base: a flag inside a
drain (`ResetAndLogAsync`) and a cancel (`RestoreRunBaseAsync`). A flagged
single-task run leaves its edits in the tree, and Reset, the command an
operator reaches for to clear them, does not reset it either. Reset also
archives the two files the resetter would need
(`pre-run-untracked.txt`, `run-base.txt`), so after a reset nothing can put the
tree back. In the validation this showed as staged work left behind after
`reset-selected`, which the operator had to `git reset -q && git checkout -- .`
away before `run-all`. The hole has a second half: the drain deliberately
skips its reset when stage 12 flagged, on the assumption that the commit
landed, and during the validation stage 12 flagged precisely because staging
failed, leaving a staged tree and no commit. Both halves close here.

## Evidence

- Validation of 2026-09-11 (item 1): after `reset-selected` on a flagged task
  the tree still held the run's staged work; the bundle had been captured.
- c0b444f6: "an ignored relay directory plus a manifest spanning two top level
  directories made staging exit one; every task on two of three evaluation
  repositories was flagged at the commit stage". A stage-12 flag after a failed
  `git add` is a staged tree with no commit.
- `Queue/RelayQueueController.PrivateHelpers.cs:26-38` `ResetAndLogAsync`:
  "When stage 12 is Flagged, the commit already landed … Skip the reset", logged
  as `reset-skipped-commit-flagged` in `.relay/<drainRunId>.log`
  (`Logging/DrainSummaryLog.cs:24-26`). The check reads `status[11].Status ==
  "Flagged"` and never asks whether HEAD moved. Only the execute phase of a
  drain (Run All) calls it (`RelayQueueController.cs:255`); since 7a7dc226 a
  planning flag leaves the checkout alone.
- Corrected on 2026-09-17: `run-selected` and `resume` are NOT drains. They
  build a driver directly (`ViewModels/MainWindowViewModel.Execution.cs:53-58`,
  `MainWindowViewModel.RunOne.cs:42-86`) and never reach `ResetAndLogAsync`.
  The driver's flag path saves the work into the bundle and leaves the tree as
  it is (`RelayDriver.Events.cs:146-150`); only a cancel resets it
  (`RelayDriver.Cancel.cs:124-128`). So after any flagged single-task run the
  run's edits stay in the checkout, Reset is the operator's only tool for
  clearing them, and today it does not. A resume copes with either state: when
  the edits are still there, the restore's `cherry-pick -n` refuses, no file is
  unmerged, and the restore reports success
  (`FlaggedWorkStore.Restore.cs:70-89`).
- `ViewModels/MainWindowViewModel.Reset.cs:9-25` `ResetSelectedTaskAsync`:
  confirm, `RelayTaskRepository.ResetTask` (`Tasks/RelayTaskRepository.Reset.cs:12-21`,
  one `Directory.Move` of `.relay/<task>/` to `.relay/<task>.reset-<stamp>/`),
  `RemoveFromSeen`, reload. No git call. The confirm text (`:16`) promises
  "it won't be lost" and "start fresh from stage 1"; it says nothing about the
  tree.
- The retired spec for the reset button (`10-reset-flagged-task-button`, in
  the history before 09e55431) records why the bundle matters: a post-flag
  reset once deleted three authored test files and the bundle was their only
  record.

## Current state (researched at d5bf93cc)

- `Execution/WorktreeResetter.cs:30-100` `ResetAsync(rootPath, taskId,
  tasksDir, git, ct)`: `git reset -q HEAD` and `git checkout -- .` (`:40-41`,
  unconditional), then deletes untracked files absent from
  `.relay/<task>/pre-run-untracked.txt` (`:44-82`), never `.relay/`,
  `.relay-scratch/` or the tasks directory; with no snapshot it deletes nothing
  and reports `SnapshotMissing` (`:54-60`). The result names `Removed` and
  `Failed`. `RelayDriver.Cancel.cs:113-118` is the driver's wrapper.
- `Execution/FlaggedWorkStore.CaptureAsync` (`:20-148`) needs `run-base.txt`
  (`:31-36`) and overwrites `flagged-work.bundle`; `FlagAsync` calls it before
  the marker (`RelayDriver.Events.cs:145-146`), so a stage-12 flag has a bundle
  of the staged tree. `RestoreAsync` applies a bundle with `cherry-pick -n`
  (`:203-204`), that is, staged.
- `CanResetSelectedTask` (`Reset.cs:27-30`) is true while a run is active
  (`MainWindowViewModelResetTests.CanReset_TrueEvenWhenIsBusy` `:80`,
  `CanReset_TrueEvenWhenAnotherTaskIsRunning` `:97`); the running task owns
  the tree then.
- `ControlApi.cs:65` lists `reset-selected` among the confirm-gated commands;
  `Reset_ViaApi_WithConfirm_CompletesAndResets_WithoutDialog` (`:193`) pins
  that the answer waits for the effect. `ResetFlaggedTask_ArchivesRunDir_AndShowsPending`
  (`:17-48`) asserts the archive and the listing, nothing about the tree.
- `RelayTaskRepository` has no git; the view model constructs `new GitInvoker()`
  elsewhere (`Bootstrap.cs:37`).

## Prescribed approach

1. `RelayTaskRepository.ResetTask` stays what it is: the archive move. The
   tree work goes into a new `Execution/FlaggedTaskReset.cs`:
   `internal static async Task<FlaggedTaskResetResult> ResetAsync(string
   rootPath, string taskId, string? tasksDir, IGitInvoker git, CancellationToken ct)`
   with `record FlaggedTaskResetResult(bool TreeReset, IReadOnlyList<string>
   Removed, bool SnapshotMissing, string? Failure)`. In order: read the
   flagged stage from `NEEDS-REVIEW` (0 when absent); `FlaggedWorkStore.CaptureAsync`
   with that stage, so the archived bundle is the tree as it stands at reset
   time, not as it stood at flag time; `WorktreeResetter.ResetAsync`; then
   `ResetTask`. Capture and reset run before the move because the move takes
   `run-base.txt` and `pre-run-untracked.txt` with it. A git failure in the
   reset step is reported in `Failure` and the archive still happens; a
   failed capture is not a reason to stop, exactly as at flag time.
2. `ResetSelectedTaskAsync` calls it when no run is active. While `IsBusy`
   the running task owns the tree, so the command archives only (today's
   behavior) and the status says so:
   `Reset <task>; the working tree was left as is because a run is active`.
   Idle: `Reset <task>; working tree restored to the run base (removed 3
   untracked files)`, or `… (untracked files kept: no pre-run snapshot)` when
   `SnapshotMissing`, or `… tree reset failed: <error>`. The confirm text
   gains the sentence "The working tree goes back to the run base; the run's
   edits are kept in the archived bundle." The `IsBusy` fork is stated in the
   button tooltip beside the existing drain-boundary sentence.
3. The drain's stage-12 skip becomes a fact, not an assumption:
   `ResetAndLogAsync` skips only when `git rev-parse HEAD` differs from
   `.relay/<task>/run-base.txt` (the sealed commit landed); a stage-12 flag
   with HEAD still at the run base is reset like any other, logged
   `reset-after-commit-flag`. The `reset-skipped-commit-flagged` line keeps
   its wording for the landed case.
4. AGENTS.md's destructive-commands sentence (`:141-143`) gains: `reset-selected`
   also restores the working tree to the run base when no run is active, after
   re-capturing it into the archived bundle. TROUBLESHOOTING.md gains a
   "Reset left files in the tree" entry naming the busy case and the missing
   snapshot case.

## Tests

- `FlaggedTaskResetTests` (GitSim): `ResetAsync_RestoresTrackedEdits_AndRemovesTheRunsUntrackedFiles`
  (a staged edit and a new file after the run base; afterwards `git status
  --porcelain` is empty but for `.relay/`); `ResetAsync_KeepsPreRunUntrackedFiles`;
  `ResetAsync_RecapturesTheBundleBeforeResetting` (the archived bundle's
  snapshot commit contains the edit made after the flag);
  `ResetAsync_WithoutASnapshot_ResetsTrackedFilesAndReportsIt`;
  `ResetAsync_ArchivesEvenWhenGitFails` (a failing invoker: the run directory
  is still moved and `Failure` is set).
- `MainWindowViewModelResetTests`: `ResetFlaggedTask_Idle_RestoresTheWorkingTree`
  (GitSim root with a staged edit; after the command the edit is gone and
  `StatusText` names the run base); `ResetFlaggedTask_WhileBusy_ArchivesOnly_AndSaysWhy`;
  `Reset_ViaApi_WithConfirm_RestoresTheTree`; `ResetFlaggedTask_ArchivesRunDir_AndShowsPending`
  keeps passing.
- `RelayQueueControllerResetTests.CommitStageFlag_WithHeadAtTheRunBase_ResetsTheTree`
  and `CommitStageFlag_AfterALandedCommit_SkipsTheReset` (the drain summary log
  line is asserted in each).

## Verification (through the control API)

Clone gorilla/mux as `mux.prep.md` describes, `open-folder`, `bootstrap`,
patch `testCmd`, then make a task flag at stage 12 the way c0b444f6 did:
create a task whose plan touches two top-level directories and, before
`run-selected`, add `.relay/` to the clone's `.gitignore` as an ignored
directory the old staging tripped on; alternatively `cancel` the run during
stage 6 and take the cancel path. Then:

    curl -s http://127.0.0.1:8765/state | jq '.selectedTask.reviewReason'
    git -C <clone> status --short | head
    curl -s -X POST -d '{"confirm":true}' http://127.0.0.1:8765/command/reset-selected
    curl -s http://127.0.0.1:8765/state | jq -r .statusText
    git -C <clone> status --short
    ls <clone>/.relay/ | grep reset-

Expected: before the reset the tree shows the run's files; after it
`statusText` says the tree was restored and how many files were removed,
`git status --short` shows nothing but `?? llm-tasks/`, and the archived
directory holds `flagged-work.bundle` whose `git bundle list-heads` names
`refs/relay-snapshot/<task>`. Then `run-all` without any manual git command
and confirm the task commits. Put the file count and the reason in the body.

## Out of scope

Resetting a task that is not flagged; `mark-done`'s tree handling; the
`resume` restore's staging (`cherry-pick -n` is what a resumed stage expects);
whether the drain should refuse to start a task on a dirty tree.

## Rejected alternatives

- Reset the tree inside `RelayTaskRepository.ResetTask`: the repository knows
  paths, not git, and the archive move must come last, after the resetter has
  read the files the move takes away.
- Reset the tree while a run is active: the running task's edits live in the
  same tree; the archive-only fork with a status line is the honest answer
  until the run ends.
- Skip the re-capture because the flag already captured: the operator may have
  edited after the flag, and "it won't be lost" is the promise the modal makes.
- Leave the stage-12 skip alone and rely on the new reset: the drain would
  still start the next task on the staged tree; the skip was written for a
  landed commit and now checks for one.
