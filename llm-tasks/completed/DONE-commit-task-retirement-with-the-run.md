# Completed tasks resurrect into the queue because the DONE rename is never committed

A completed task's file rename (`<id>.md` → `DONE-<id>.md`, or the archive move into
`completed/batch-N/`) happens only **on disk, after the task's git commit**, and nothing
ever commits it. `RelayDriver.RunTaskAsync` commits the work with an explicit file set
(manifest + `.relay/<id>/` proof files — `RelayDriver.cs:240-248`), *then* calls
`TaskCompletionArchive.CompleteAsync` (`RelayDriver.cs:251`) which does a bare
`File.Move` (`TaskCompletionArchive.cs`, `MarkDone`). HEAD therefore still contains the
open task file; the retirement exists only as uncommitted worktree state that a human or
agent must remember to fold into a later `chore(tasks)` commit.

Observed consequence (2026-06-09, installer-4): the pipeline committed the work at 13:54
(`d38278c`) and renamed the file on disk; a routine worktree cleanup at 14:22 (checkout/
reset between drives) **resurrected the original task file from HEAD**; the 15:55 handoff
commit (`01cae7d`) then staged the `DONE-` copy without the deletion. Result: an
already-completed task sat in the committed queue as open — `git log` shows
`llm-tasks/installer-4-...md` and its `DONE-` twin coexisting — and the next drive would
have re-run the whole pipeline against it. (Forensics: the `DONE-` file's mtime predates
the completion commit because `rename(2)` preserves mtime — proving rename-not-copy, and
that the original was re-created later.)

Latent second bug in the same path: `MarkDone` uses `File.Move(src, dst)` with no
already-done handling — if `DONE-<id>.md` already exists (crash between rename and a
later retry, or a manual copy), `CompleteAsync` throws, is caught, and the run logs
`done_rename_failed` forever; the task file is never retired.

## Goal

Task retirement is durable and atomic with the task's own commit: after a successful run,
HEAD contains the rename/archive move (old path deleted, `DONE-`/archived path added) in
the **same commit** as the work, for both flat task files and nested task directories,
with `archiveOnDone` on or off. Re-running completion when the file is already retired is
a no-op (idempotent), and a worktree `git checkout .` after completion can no longer
resurrect a finished task. Regression-tested.

## Approach (suggested)

- Reorder: perform the retirement rename/move **before** `GitCommitter.CommitAsync` and
  include both sides of the move in the commit's file set (the deletion of
  `llm-tasks/<id>.md` and the addition of the `DONE-`/archived path), alongside the
  existing proof files. If the commit then fails, the flag path already reports it — but
  consider restoring the original name on failure so a flagged task stays runnable.
- Make `MarkDone`/`Archive` idempotent: if the source is gone and the destination exists,
  treat as already-retired and return it; never throw on a pre-existing `DONE-` file.
- Keep `task_done`/`task_archived` events as today; `done_rename_failed` should become
  rare and actionable.
- Tests, mirroring `GitCommitterTests`/`RelayDriverGitCommitTests` style: (a) completion
  commit contains `D llm-tasks/<id>.md` and `A llm-tasks/DONE-<id>.md`; (b) with a
  `completed/batch-N` layout the archived path is committed instead; (c) nested task dir
  variant; (d) idempotency — completing twice, or completing when `DONE-` already
  exists, succeeds without error; (e) after completion, `git checkout -- llm-tasks/`
  does not bring the task back.
