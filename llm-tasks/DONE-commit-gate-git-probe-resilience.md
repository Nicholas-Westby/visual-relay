# One transient git-probe failure kills a finished run at the commit gate — blind

Observed 2026-06-09 05:59: parallelize-planning-across-tasks completed stages 1–10
(67 min, $0.82, final gate suite 444/444 green) and then flagged at stage 11 with
`target root is not a git repository`. The repo was a perfectly healthy git checkout
seconds before and after (every prior stage ran git, and manual `git rev-parse
--is-inside-work-tree` succeeded immediately post-mortem). The entire implementation was
left uncommitted in the worktree; only a manual resume salvaged it.

Two defects:

1. **No resilience**: `GitCommitter.CommitAsync` treats its opening probe
   (`git rev-parse --is-inside-work-tree`, `GitCommitter.cs:22`) — and by extension any
   single git invocation — as infallible truth. A transient environment hiccup (this
   host is a Tart VM on virtio-fs, whose dentry cache is documented to one-shot-fail
   directory enumeration under churn; an AV scan or a stale NFS handle would do the same
   elsewhere) becomes a terminal flag for an otherwise-finished run.
2. **No diagnostics**: the flag reason carries only the interpreted message, not git's
   exit code or stderr. Post-mortem had nothing to distinguish "git exited 128: not a
   git repository" from "spawn failed" from "stderr: fatal: unable to read tree".
3. **No resume-at-commit** (observed on the salvage attempt, 23:10): re-running the task
   resumed only the read-only planning stages and threw the run back to stage 5 —
   Author-tests/Implement/Review/Fix/Verify all re-execute even though the seal chain
   records stages 1–10 Done and the gate suite was green seconds before the flag. A
   stage-11-only failure should be re-entrant: re-run the gate suite, re-verify the
   recorded task hash against the worktree, and proceed straight to commit; fall back to
   the conservative stage-5 restart only when the hash/gate no longer matches.

## Goal

The commit gate never discards a finished run over a transient probe/command failure:
fail-fast git invocations in the commit path retry briefly on failure, and any terminal
git-derived flag reason includes the underlying exit code and stderr verbatim. A
repository that is *genuinely* not a git repo still flags promptly (after the short
retry window) with that evidence attached.

## Approach (suggested)

- In the GitCommitter path (probe + subsequent porcelain calls): on non-zero exit,
  retry 2–3× with a short backoff (e.g. 250ms/1s) before treating the result as truth.
  Keep total added latency trivially small for the genuine-failure case.
- Append `(git exit <code>: <stderr first lines>)` to every git-derived flag reason in
  this path (probe, add, commit, rev-parse of the new sha). The existing
  `ErrorHintClassifier` hint machinery can carry remediation text.
- Consider the same wrapper for `ActiveTaskLock`/pre-run git probes — anywhere one git
  exit decides run fate.
- Resume-at-commit: when a task's status records stages 1–10 Done and only stage 11
  failed, the driver re-validates (gate test run + recorded task-hash match against the
  current worktree/seals) and re-enters at the commit step instead of restarting the
  mutation phase; mismatch → today's conservative restart.
- Tests (extend `GitCommitterTests`): (a) probe fails twice then succeeds → commit
  proceeds, no flag; (b) probe fails persistently → flag, reason contains exit code and
  stderr text; (c) downstream call (e.g. `git add`) transient-fails then succeeds →
  commit proceeds; (d) latency bound: persistent-failure path completes within ~seconds;
  (e) resume with 1–10 Done + matching hash goes straight to commit; (f) resume with a
  dirty mismatch restarts at stage 5 as today.
