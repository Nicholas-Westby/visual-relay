# Make resume after a rejected commit leave consistent state

One i18next drain on the Windows arm exercised the path "commit rejected by the
project's hook, then resume" and left five inconsistencies behind. None lost
work, but each makes the next run or the operator's reading of the state
wrong. This task fixes them together because they share the flagged-work
restore and resume code.

## Evidence

Windows arm, i18next, 2026-09-14, builds 0.367 (drain) and 0.372 (resume).
Evidence on the Windows PC at `~/vr-eval/i18next-run1-evidence`.

1. After task 1's commit was rejected by husky (`.husky/pre-commit: 1: npx:
   not found`, fixed since in 7a8f70f1), the index held
   `AD llm-tasks/completed/<task1>/DONE-<task1>.md` (added in the index,
   deleted in the worktree) while task 2 ran. Task 2 happened not to carry
   it, but nothing prevents a later commit from doing so.
2. Resuming task 1 restored its flagged work with `flagged_work_restored
   result=conflicts` (task 2's commit had changed `vitest.config.mts`), and
   afterwards the index had `.relay/.gitignore` and `.relay/config.json`
   staged as added files, VR's own bookkeeping.
3. The same resume's stage 12 logged `verify_result command=npm test
   check=green reason=setup check failure`: a green check with a failure
   reason.
4. The conflict resolver's answer had no JSON (`contract_reask error=no JSON
   object found in the answer; the contract block is required`), and the run
   then started again at stage 5 with a new `run_start`, without an event or
   status line saying why it restarted.
5. `drainHalted=true` with the old `haltReason` ("commit gate rejected
   consecutive tasks ... npx: not found") stayed in `/state` after task 2's
   resume committed, and across an app relaunch.

## Current state (researched at 37403a8a)

- Flagged-work restore and the conflict resolver:
  `Execution/RelayDriver.FlaggedWork.cs:40-50` (`flagged_work_restored`) and
  `:100-130` (`resolve-conflicts-attempt{n}` invocation).
- The verify reason text: `Execution/RelayDriver.VerifyObservability.cs:149-192`
  returns "setup check failure" variants from the setup-check results
  regardless of the check value passed alongside.
- Halt state in the API: `App/Services/ControlApi.State.cs` (`drainHalted`,
  `haltReason`); an earlier finding noted the `DRAIN-HALTED` marker is only
  cleared by the next `run-all`.
- The earlier spec `reset-selected-restores-the-working-tree` covered the
  index a flagged run leaves behind for reset, not for a following task.

## Prescribed approach

1. Before a task's commit stage stages anything, unstage index entries that
   belong to no path of this task: entries under `.relay/`, and task
   bookkeeping (`<tasksDir>/completed/<other-task>/`) of other tasks. Publish
   `commit_index_cleaned` with the paths when any were removed.
2. The flagged-work restore never stages `.relay/`: restore those paths
   unstaged, or skip them (they are regenerated), and pin it with a test that
   restores a snapshot containing `.relay/config.json`.
3. `verify_result` carries a reason only when the check is red; a green check
   with setup-check notes puts them under a separate `notes` field.
4. When conflict resolution fails and the run restarts from an earlier stage,
   publish `flagged_work_restart` with `fromStage` and the reason (resolver
   contract failure, unresolved conflicts), and set the status line to say so.
5. A task that commits clears `drainHalted` and `haltReason` (the marker
   file and the in-memory state), so a successful resume ends the halt.

## Tests

- `RelayDriverCommitIndexTests` (git simulator): an index holding another
  task's DONE file and `.relay/config.json` commits only the task's paths and
  publishes `commit_index_cleaned`.
- `FlaggedWorkStoreTests`: restoring a snapshot that includes `.relay/`
  leaves nothing under `.relay/` staged.
- `VerifyObservabilityTests`: green with setup notes has no `reason`.
- `RelayDriverFlaggedWorkRestartTests`: resolver contract failure publishes
  `flagged_work_restart fromStage=5`.
- `MainWindowViewModelTests` drain halt: a committed task clears the halt;
  `/state` reads `drainHalted=false` afterwards.

## Verification (through the control API)

Mac: in a repo with a husky `pre-commit` that exits 1, run two tasks; after
the first is rejected, remove the hook, resume the first with `select-task`
plus `resume`, then run the second. Expected: no `.relay/` or other task's
DONE file in either commit (`git show --stat`), `drainHalted=false` after the
resumed commit, and the events above in `run.log`.

## Out of scope

Why the resolver answered without JSON (model behaviour; the contract re-ask
already exists), and the husky PATH itself (fixed in 7a8f70f1, verified on
Windows by task 2's resumed commit 8157f31).
