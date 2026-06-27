# A task editing swival.toml breaks its own later stages — pin the profile per run

Proven 2026-06-10 23:06 by the drop-vestigial-kimi-suffix run: stage 6 (correctly,
per its task) renamed tier aliases everywhere, including the repo-root `swival.toml`
that `SwivalProfileSession.PrepareAsync` honors. Stage 8's spawn then requested
model `balanced` from the live backend, which still serves only `balanced-kimi`
(the backend swap is deliberately an operator action at a drive boundary), got
litellm 400 "Invalid model name", and the stage died with swival exit 1 → flag.
General class: any task whose diff touches the pipeline's own launch configuration
changes the behavior of its OWN later stages mid-run.

## Goal

A run's swival launch profile is immutable for the duration of that run: stages 5–11
launch with the same profile content that stage 1 launched with, regardless of what
the task's edits do to profile sources in the working tree. The task's edits still
land in the commit untouched — only the LAUNCH path is pinned.

## Approach (suggested)

- Snapshot the effective profile content once at run start (RelayDriver level);
  have PrepareAsync write that pinned content for every subsequent attempt instead
  of re-reading the working tree each time, restoring the tree's version after each
  attempt as it does today.
- Emit an info event when the pinned content differs from the tree's current
  version (so the operator knows a backend/profile swap is pending at the boundary).
- Tests: a fake task edit to swival.toml between stages must not change the profile
  the next stage launches with; the working tree's edited file survives to commit;
  the divergence event fires.
