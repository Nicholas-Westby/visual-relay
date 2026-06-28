# Harness: the verify worktree must propagate file DELETIONS (stop resurrecting deleted files)

## Problem
Verify (stages 9/10) runs in an isolated worktree built by checking out HEAD and then
**overlaying the task's uncommitted changes**. The overlay COPIES modified + untracked files
onto that HEAD checkout, but it does **not** apply DELETIONS — so any tracked file the task
removed is **resurrected from HEAD** inside the worktree. The agent's correct deletion is
silently reverted under it, the suite is verified against a tree that still contains the
deleted file, and the task can **never pass verify** — it flags through no fault of its own.

### Evidence (root cause confirmed)
- `src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktree.cs`: `EnumerateUncommittedAsync`
  collects only `git diff --name-only` (modified) + `git ls-files --others` (untracked). The
  overlay copy loop then **skips** any such path that "no longer exists on disk" — the comment
  there explicitly says it copies "modified files … not deletions." Deletions are dropped.
- Observed live: the `harness-revert-split-verify` task correctly `git rm`'d
  `tests/VisualRelay.Tests/VerifyBuildWarmTests.cs` (gone from the main checkout), but **every**
  fix-verify attempt recompiled it inside the worktree → repeated `CS1061` (it references
  `RelayConfig.BuildCommand`, which the same task removed) → `Build FAILED` → the task looped
  to the verify-loop limit and flagged. Deletion of an *unreferenced* file (e.g. the accordion
  task's dead converters) is harmless and slips by; deletion of a file that references a removed
  symbol is fatal.

## What to do
Make the verify-worktree overlay apply deletions as well as additions/modifications:
- Detect deleted tracked paths (e.g. `git diff --name-status` / `git ls-files --deleted`) and
  **remove** them from the worktree after the HEAD checkout (handle empty parent dirs; handle a
  deleted directory). Keep the existing copy/symlink behavior for modified + untracked files and
  the build-output skip-list (`bin`/`obj`/`target`/…).
- Confirm the worktree's tree then matches the main checkout's working tree (modulo the
  intentionally-skipped build-output dirs) for tracked content.

## Done when
- A task that deletes a tracked file (especially one whose removed symbols are referenced
  elsewhere and also removed) verifies correctly — the deletion is reflected in the worktree.
- A regression test covers it: in a temp git repo, delete a tracked file, build the overlay
  worktree, assert the file is absent.
- General-purpose (keys on git status, not VR specifics); `./visual-relay check` green; suite
  green under nono; Conventional Commit.

## Priority
High — this silently breaks verify for ANY deletion-bearing task. It is currently blocking
`harness-revert-split-verify` (which can be re-run once this lands).
