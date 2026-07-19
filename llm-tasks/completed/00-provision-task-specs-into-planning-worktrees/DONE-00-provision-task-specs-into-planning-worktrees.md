# Provision pending task specs into planning worktrees

Plan-phase runs never see the task spec. `PlanningWorktree.CreateAsync` checks
out a detached HEAD (committed files only) and `CopyConfigIntoWorktree` copies
in exactly one file — `.relay/config.json`. Pending specs are untracked until
the stage-12 seal retires them into `llm-tasks/completed/`, so inside the
worktree the repository lookup returns null and the driver builds an empty
`RelayTaskInput` (RelayDriver.cs:39-40). Two observed consequences:

1. Silent fabrication (the pre-existing bug): the empty-input gate's
   plan-worktree exemption (RelayDriver.TaskInputGate.cs:33) keys on the whole
   tasks dir being absent, so before any task ever sealed, plan passes ran
   stages 1-4 against a blank input. Proof: patternsmith
   `.relay/limit-height-of-source-text-input/stage1-attempt1.input.json`
   (drain 20260719035606) has a worktree working directory and an empty
   `## Task input` section — Ideate through Plan worked from nothing but the
   kebab task id in the prompt header. This is exactly the
   fabricate-from-an-empty-prompt incident (2026-07-12) the gate exists to
   prevent; execute stages 5-12 rerun in the main repo with the real spec,
   which masked it.
2. Guaranteed false flags (the new symptom): the moment a repo's first task
   seals, `llm-tasks/completed/` enters HEAD, every later planning worktree
   HAS an `llm-tasks/` dir, the exemption goes dead, and every drain flags
   every pending task `empty_task_input` ~250ms in — no LLM call. Observed:
   patternsmith drain 20260719203406 flagged both queued tasks while the GUI
   showed their full markdown; the seals at bc3f924/ce608bf the night before
   are what armed it. Every repo crosses this line permanently after its
   first seal, including this one.

## Prescribed approach

Copy the pending task's spec into the planning worktree right after
`CopyConfigIntoWorktree` (PlanPhaseRunner.cs:133-137), then delete the
now-obsolete plan-worktree exemption from the gate so a missing spec flags
loudly everywhere instead of planning from nothing. The rewrite worktree
(TaskRewriteRunner) already provisions and copies back the task folder; this
brings the plan phase up to the same standard.

### Steps

1. `PlanningWorktree`: new `CopyTaskSpecIntoWorktree(mainRepoRoot,
   worktreePath, taskId, ct)` (or similar) that resolves the task from the
   MAIN repo via `RelayTaskRepository` and replicates it at the same relative
   path in the worktree. Both shapes: folder tasks
   (`<tasksDir>/<id>/<id>.md` plus ALL siblings — attachments are read by
   stages via SiblingPaths, so copy the folder recursively) and flat tasks
   (top-level `<tasksDir>/<id>.md`). Resolve `tasksDir` from config, not a
   literal `llm-tasks`. Overwrite anything HEAD happened to contain.
2. Unlike the config copy, this must NOT be silent best-effort: if the task
   cannot be resolved or the copy throws, let the failure surface —
   PlanOneAsync's per-task catch already converts it to a Failed outcome
   without killing the drain, and a spec absent at run start is now a real
   error the gate should flag.
3. `RelayDriver.TaskInputGate.cs`: remove the
   `task is null && !Directory.Exists(tasksDir)` exemption and its comment.
   After step 1 a plan worktree always has its spec; a null task or blank
   markdown is genuinely the deleted/empty-spec incident in every context.
4. Audit for other callers relying on the exemption (tests included) and
   update them; `RelayDriverEmptyTaskInputTests` likely encodes the old
   behavior.

## Tests (red first)

- Provisioning, folder task: main repo with a committed
  `llm-tasks/completed/` plus an untracked pending folder task with one
  attachment → after provisioning, the worktree contains spec + attachment,
  and the worktree-scoped repository lookup finds the task with the real
  markdown.
- Provisioning, flat task: top-level `<id>.md` variant lands at the same
  relative path.
- End-to-end plan pass (git-sim, as in existing PlanPhaseRunner tests): the
  stage-1 input persisted in the worktree contains the pending task's actual
  markdown under `## Task input` — not blank.
- Gate without exemption: worktree with NO tasks dir and a null task →
  flagged `empty_task_input` (the previously exempted case).
- Copy failure: provisioning throws for one task → that task's outcome is
  Failed/Flagged, the drain completes the other tasks.

## Verification

`./visual-relay check` green. Manual: in a repo whose HEAD already contains
`llm-tasks/completed/` (patternsmith qualifies), add a new uncommitted task
and Run All — the plan phase runs stages 1-4, `stage1-attempt1.input.json`
shows the real spec text, and no `empty_task_input` flag appears.
