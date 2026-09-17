# Make resume after a rejected commit leave consistent state

One i18next drain on the Windows arm exercised the path "commit rejected by
the project's hook, then resume" and left five oddities behind. None lost
work. The code was read again on 2026-09-17 and each oddity traced to its
cause: one is already harmless and only needs a test, and the other four are
small faults in the resume path. This task fixes those four and pins the
fifth.

## Evidence

Windows arm, i18next, 2026-09-14, builds 0.367 (drain) and 0.372 (resume).
Evidence on the Windows PC at `~/vr-eval/i18next-run1-evidence`.

1. After task 1's commit was rejected by husky (`.husky/pre-commit: 1: npx:
   not found`, fixed since in 7a8f70f1), the index held
   `AD <tasksDir>/completed/<task1>/DONE-<task1>.md` (added in the index,
   deleted in the worktree) while task 2 ran.
2. Resuming task 1 logged `flagged_work_restored result=conflicts` (task 2's
   commit had changed `vitest.config.mts`), and afterwards the index had
   `.relay/.gitignore` and `.relay/config.json` staged as added files.
3. The same resume's stage 12 logged `verify_result command=npm test
   check=green reason=setup check failure`: a green check with a failure
   reason.
4. The run then started again at stage 5 with a new `run_start`, and no event
   or status line said why.
5. `drainHalted=true` with the old `haltReason` ("commit gate rejected
   consecutive tasks ... npx: not found") stayed in `/state` after task 2's
   resume committed, and across an app relaunch.

## Current state (researched at f8a0d07b)

- Item 1 cannot reach a commit. `Execution/GitCommitter.cs:66` runs
  `git reset -q` before it stages anything, then stages the manifest
  (`add -A -- <manifest>`), tracked changes outside `.relay`
  (`add -u -- . :(exclude).relay`) and the retirement files. An entry that was
  only ever added to the index is gone after that reset. No test pins it:
  `GitCommitterTests` has six facts and none starts from a dirty index.
- Item 2 comes from the conflict path, not from the restore itself.
  `Execution/RelayDriver.FlaggedWork.cs:121` runs `git add -A` on the whole
  tree after each resolver attempt, so that `ls-files -u` can see what is
  still unmerged. That also stages VR's own untracked files. The commit
  stage's reset keeps them out of VR's commit, but until then `git status`
  shows them staged, and a commit made by hand would take them.
- Item 3: `Execution/RelayDriver.VerifyObservability.cs:46-49` picks the
  reason from the exit code alone. A zero exit takes
  `BuildSetupCheckFailureReason(setupChecks)`, which returns the literal
  "setup check failure" when `setupChecks` is null (`:153-156`). The
  commit-gate resume publishes without setup checks
  (`RelayDriver.CommitGate.cs:56-58`), so every green resume at stage 12
  carries that reason.
- Item 4: `RelayDriver.CommitGate.cs:20-130`
  (`ValidateCommitGateResumeAsync`). A resume at stage 12 runs verify again
  and compares the working tree with the hash stage 11 recorded. When verify
  passes and the hash differs (here because task 2's commit had changed a file
  of task 1's manifest), it sets `firstStageToRun = 5`, drops the seals after
  stage 4, marks stages 5 to 12 waiting and writes three ledger lines
  ("Resume fallback: commit-gate re-validation failed ... Restarting from
  stage 5"). No event and no status text. `run_start` (`RelayDriver.cs:60-62`)
  carries only `version`, so a resumed run's log does not even say it is a
  resume. The restore that follows (`RelayDriver.cs:54`) is what then met the
  conflicts of item 2.
- Item 5: `DrainCircuitBreaker.ClearHaltMarker` has one caller,
  `Queue/RelayQueueController.cs:81`, at the start of a drain. Resume and
  run-selected build a driver directly
  (`ViewModels/MainWindowViewModel.RunOne.cs:42-86`) and never pass through
  it. `/state` reads the marker file live (`Services/ControlApi.State.cs:23`,
  `:66-67`), which is why the halt survived a relaunch.

## Prescribed approach

1. Pin what already holds. No production change: a test proves that an index
   holding another task's added `DONE-` file and a staged `.relay/config.json`
   produces a commit with neither.
2. The conflict path stages only what it has to. Replace the whole-tree
   `git add -A` with `git add -- <conflictedFiles>`; `ls-files -u` reads the
   same index, so the remaining-conflict check is unchanged. Whatever else the
   resolver touched stays an ordinary working-tree edit, which is what the
   stages after it expect.
3. A reason belongs to a red check. In `PublishVerifyResultAsync` the reason
   follows `check`, not the exit code: green gives an empty reason; red with a
   failing exit keeps `ExtractFailureReason`; red with a zero exit (a guard or
   bootstrap failure) keeps `BuildSetupCheckFailureReason`.
4. A resume says where it starts and why. `run_start` gains `resume`
   (`true`/`false`) and `firstStage`. When the commit-gate fallback moves a
   resume back to stage 5 it publishes a warn event `resume_restart` with
   `fromStage=12`, `toStage=5` and `reason=working tree changed since verify`
   (or `commit-gate verify failed` on that branch), and the status line reads
   `Resuming <task> from stage 5: the working tree changed since it was
   verified`. The ledger lines stay.
5. A commit ends the halt. When a task commits, the driver's caller clears the
   halt marker, whichever path ran it: the drain already does at its start;
   the single-task path (`RunOneAsync`) calls
   `DrainCircuitBreaker.ClearHaltMarker` after an outcome of `Committed`. A
   flagged or cancelled single run leaves the marker alone.

## Tests

- `GitCommitterTests.CommitAsync_WhenTheIndexHoldsAnotherTasksFiles_CommitsNeitherOfThem`
  (git simulator): the index holds an added `completed/other/DONE-other.md`
  and an added `.relay/config.json`; the commit lists only the task's paths.
- `RelayDriverResumeConflictStagingTests` (new, driver fakes): after a
  conflicted restore and a resolver attempt, the recorded git calls contain
  `add -- <the conflicted files>` and no `add -A`; an untracked
  `.relay/config.json` is not in the index afterwards.
- `VerifyObservabilityTests`: green with null setup checks publishes an empty
  `reason`; red with exit 0 and a red guard keeps the guard reason; red with
  exit 1 keeps the extracted failure line.
- `RelayDriverResumeCommitGateVerifyTests`: a resume at stage 12 whose tree
  hash differs publishes `resume_restart` with both stages and the reason, and
  `run_start` carries `resume=true` and `firstStage=5`; a fresh run carries
  `resume=false` and `firstStage=1`.
- `MainWindowViewModelTests` drain halt: with a halt marker on disk, a
  single-task resume that commits removes it and `/state` reads
  `drainHalted=false`; one that flags leaves it.
- Watch each fail first.

## Verification (through the control API)

Mac: in a small repo with a husky-style `pre-commit` that exits 1, create two
tasks that touch the same file and `run-all`; both are rejected and the drain
halts. Remove the hook, `select-task` the second and `resume`, then the first
and `resume`. Expected in the first task's `run.log`: `run_start resume=true`,
`resume_restart fromStage=12 toStage=5`, `flagged_work_restored`, and a
stage 12 `verify_result check=green` with an empty `reason`. Expected outside
it: `git status --short` shows nothing staged under `.relay/` at any point,
neither commit contains `.relay/` or the other task's `DONE-` file
(`git show --stat`), and `/state` reads `drainHalted=false` after the first
resumed commit. Put the four event lines in the commit body.

## Out of scope

Whether a changed tree should restart at stage 5 or somewhere later; why the
resolver once answered without JSON (its answer is not read, only the index
is); the husky PATH itself (fixed in 7a8f70f1).

## Rejected alternatives

- Unstaging foreign index entries before the commit, as the first version of
  this spec asked: the commit stage already resets the index, so there is
  nothing left to unstage.
- Restoring `.relay/` paths unstaged inside `FlaggedWorkStore`: the capture
  already keeps `.relay/` at the run base's versions in the snapshot
  (`FlaggedWorkStore.cs:96-105`); the staged files came from the whole-tree
  add, and narrowing that add removes the cause.
