# A resumed run's sealed commit omits files authored before the interruption

`db7d3c8` (parallelize-planning-across-tasks) landed with its core new files missing:
`PlanningWorktree.cs`, `PlanPhaseRunner.cs`, `DrainLifecycleCallbacks.cs`,
`DrainSummaryLog.cs`, `MainWindowViewModel.Stages.cs`, plus five new test files — 10
files total — were left **untracked in the worktree** while the commit's modified files
reference them. Result: **HEAD does not build from a fresh checkout**
(`RelayQueueController.cs(17,22): error CS0246: 'DrainLifecycleCallbacks' could not be
found`, reproduced in a clean git worktree 2026-06-10). The main working tree masks the
breakage because the files exist on disk there. (Repaired manually in the follow-up
commit that adds the 10 files; this task fixes the mechanism.)

Mechanism: the task's first run (which authored those files in stages 5–10) flagged at
stage 11; the salvage was a **resume** that restarted at stage 5. `RelayDriver` captures
`preRunUntracked` at *run instance* start and `GitCommitter.CommitAsync` uses it to
exclude pre-existing untracked files from auto-include (the guard that keeps operator
scratch files out of sealed commits). On the resumed instance, the files authored by the
*interrupted* instance were already untracked at start → classified as pre-existing →
excluded. Any resume after Implement has this hazard; combined with the planned
resume-at-commit (see `commit-gate-git-probe-resilience`) it would bite every time.

## Goal

A task's sealed commit contains every file the task authored across **all** of its run
instances — interrupted, resumed, or re-driven — never just the final instance's delta.
HEAD always builds after a task commits. Operator scratch files remain excluded.

## Approach (suggested)

- Persist the authored-file knowledge across instances: snapshot `preRunUntracked` (or
  better, the set difference "untracked now vs untracked at FIRST instance start") into
  `.relay/<taskId>/` at first run start; on resume, load it instead of re-snapshotting,
  so files born between first-start and now are auto-include candidates again.
- Alternative/complement: derive authored files from the union of stage manifests
  (`manifest.txt` + `amendManifest`) and treat manifest entries as authoritative commit
  content whether tracked or not; investigate why the 10 files (presumably in the
  manifest) were not staged by the manifest path in `GitCommitter.CommitAsync`.
- Post-commit invariant check (cheap, catches the whole class): after the sealed commit,
  verify no file referenced by the committed compilation remains untracked — concretely,
  flag (not silently pass) if `git status --porcelain` shows untracked files under
  source roots that appear in the commit's manifest. Consider an optional
  fresh-worktree build smoke as the strong form (ties into
  `verify-env-bootstrap-changes` machinery).
- Tests (extend `RelayDriverGitCommitTests` / `GitCommitterAutoIncludeTests`):
  (a) interrupt after a stage that authored new files → resume → commit contains them;
  (b) operator file untracked before FIRST start stays excluded across resume;
  (c) manifest-listed new file is committed even when untracked at resume start.
