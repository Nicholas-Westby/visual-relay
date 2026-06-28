# Harness: make the verify worktree build actually warm/incremental (not just split)

> **IGNORE (deferred, not abandoned).** Follow-up surfaced by code review of the completed
> `harness-verify-worktree-build-warm` (commit 97eb26b). Un-IGNORE to revive.

## Context
`harness-verify-worktree-build-warm` split verify into an untimed `buildCmd` phase + a
`testCmd --no-build` phase (good). But the verify worktree overlay still omits `bin`/`obj`
(`src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktree.cs`), so the "build phase" is
still a **cold full rebuild** — the task's primary "Done when" (incremental/warm build) is
unmet; only the timing was separated. The reviewer noted seeding `bin`/`obj` is
path-baked/timestamp-sensitive across a git worktree, so the split was a reasonable interim
choice.

## What to do
Make the worktree build genuinely incremental (e.g., safely seed/hardlink the main
checkout's warm `bin`/`obj` into the worktree, validating paths/timestamps so the agent's
changes still recompile and nothing stale is masked), then reconsider `testTimeoutMs`
downward. Must remain general (no assumption of VR's own layout; works for any target
solution). Keep `buildTimeoutMs` honored (now threaded in by commit 7b1f86c).

## Done when
- A typical verify's build phase is materially faster (incremental, not cold) with zero
  correctness loss (agent changes are compiled + tested).
- `./visual-relay check` green; Conventional Commit.
