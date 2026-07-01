# Resume Actually Restores Flagged Work

## Problem

When a task flags mid-pipeline (stages 5–10), the harness **discards the working-tree edits** so the
next drained task starts clean. `WorktreeResetter.ResetAsync`
(`src/VisualRelay.Core/Execution/WorktreeResetter.cs`) runs `git reset -q HEAD` + `git checkout -- .`
and deletes the task-authored untracked files. The per-stage seals record git tree hashes, but those
objects are not durable (the ephemeral run worktree is removed and unreferenced trees are pruned).

So today "Resume" cannot actually resume. On resume, `LoadResumeState`
(`src/VisualRelay.Core/Execution/RelayDriver.Resume.cs`) only reloads bookkeeping (status, ledger,
seals, manifest, cost, and the seal-*chain* hash) and computes `firstStageToRun` from the first
non-`Done` stage — it never restores the working tree. `ValidateCommitGateResumeAsync`
(`src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs`) is the only code that re-validates tree
state, and it is gated to `firstStageToRun == 11` (the Commit stage only). A stage-10 flag therefore
re-runs fix-verify against a **clean** tree — none of the authored tests or implementation present —
producing a vacuous or nonsensical result. (And separately, the Resume button is disabled by a
command-wiring bug; see Part 4.)

## Goal

On flag, durably snapshot the flagged working tree **off main and git-ignored**. On resume, re-apply
that snapshot onto the current working tree (3-way, conflict-aware) **before** re-entering the flagged
stage, so fix-verify continues from the real work with a fresh attempt budget. Also make the Resume
button reachable.

---

## Part 1 — Snapshot the flagged work (harness-side, hook-free, git-ignored)

**When.** At flag time, *before* the working tree is reset. Integrate in the flag path
(`RelayDriver.FlagAsync`, `src/VisualRelay.Core/Execution/RelayDriver.Events.cs`) so the snapshot is
captured regardless of who resets afterward (the drain calls `WorktreeResetter.ResetAsync` after a flag;
a single GUI run may not). The snapshot must be taken while the edits are still on disk.

**How (no hooks, no HEAD/index/working-tree disturbance).** Use git **plumbing** through the harness's
`GitInvoker` (`src/VisualRelay.Core/Execution/GitInvoker.cs`). `GitInvoker` runs `git` directly and is
**not** the sandboxed agent command path, so the command-guard middleware and the agent commit/hook-bypass
restrictions do not apply — and plumbing commands never fire hooks anyway. Build a snapshot commit
against a **temporary index** so HEAD, the real index, and the working tree are untouched:

1. `GIT_INDEX_FILE=<temp>` `git add -A` — stage tracked edits **and** task-authored untracked files.
   Reuse the same authored-file set the committer already computes (`preRunUntracked` /
   `GitCommitter.Untracked`) so pre-existing untracked files are **excluded**.
2. `git write-tree` → snapshot tree.
3. `git commit-tree <tree> -p <runBaseSha>` → snapshot commit. `runBaseSha` is the run base recorded in
   `.relay/<taskId>/run-base.txt` (HEAD at the task's first run). No hooks run for any of these.

**Store (git-ignored, off main).** Write the snapshot as a **git bundle** to
`.relay/<taskId>/flagged-work.bundle`. This path matches `.gitignore`'s `.relay/*` rule, so it is
**automatically ignored and never committed** — do **not** add it to the `CommitProofArtifacts` set in
`ExecuteCommitStageAsync`. Create it with `git bundle create <path> <runBaseSha>..<snapshotCommit>`
(records the base as a prerequisite; the base is reachable on main so unbundle can verify it). Also
write a small sidecar `.relay/<taskId>/flagged-work.json` = `{ baseSha, createdUtc, flaggedStage }` for
the resume step and diagnostics (`createdUtc` supplied by the caller; do not call `DateTime.Now` in a
way that breaks determinism guards).

**Why a bundle (not a raw patch).** A bundle carries exact objects (binary-safe, exact blobs) and a real
commit, which lets resume do a proper 3-way merge that produces conflict markers. A `git diff` +
`git apply --3way` is weaker: binary handling is lossy and it needs the base blobs present. Use a bundle.

**Robustness.** The snapshot is best-effort: if any step fails, log it and continue with the existing
reset. Snapshotting must never block or fail the flag itself.

---

## Part 2 — Restore on resume (3-way, conflict-aware)

**When.** On a resume run (`_options.Resume`), after `LoadResumeState` / `ValidateCommitGateResumeAsync`
compute `firstStageToRun`, and **before** the stage loop reaches the flagged stage — but only when
`firstStageToRun` is a mid-pipeline stage (5–10) **and** `.relay/<taskId>/flagged-work.bundle` exists.
Do not disturb the existing stage-11 commit-gate resume path.

**How.**
1. `git bundle verify <path>` — confirms the base prerequisite is reachable. If the base is gone (history
   rewritten) or verify fails, treat as un-restorable: keep the bundle, log, and fall back to a normal
   full re-run from stage 1 (do not silently pretend to resume).
2. Fetch the snapshot commit from the bundle into the object DB (e.g. `git fetch <bundle> <ref>`),
   capturing the snapshot SHA.
3. 3-way apply the delta `base → snapshot` onto the **current** working tree (clean, at current HEAD,
   which may have advanced past the base). Recommended mechanism: `git cherry-pick -n <snapshot>` (a
   3-way merge with base = the snapshot's parent, ours = current HEAD, theirs = snapshot), then
   `git cherry-pick --quit` to clear the sequencer state while **keeping** the applied changes in the
   working tree. (`git merge-tree` / `read-tree -m -u` are acceptable equivalents.)
4. Detect conflicts via unmerged paths (`git ls-files -u`) and/or conflict markers.

**Clean apply →** proceed to `firstStageToRun` (the flagged stage) normally. Fix-verify gets its fresh
3-attempt budget, now operating on the restored work re-based onto current HEAD.

**Conflict → conflict-aware resume (do not discard, do not silently fall back).** The base moving while
a task was flagged is expected (a real run advanced from one HEAD to another with unrelated commits in
between). Leave the conflict markers in the working tree and run a dedicated **conflict-resolution step
before the flagged stage**:
- A harness-driven subagent turn (a new stage/driver step, modeled on the existing stage runners) given
  the list of conflicted files (markers in place), the task intent, and the ledger, and instructed to
  resolve every conflict so the intended feature is preserved and adapted to the upstream changes.
- The resolver is an ordinary agent turn: it may **not** commit or bypass hooks (the harness owns all
  git writes, as everywhere else).
- After the turn, a guard verifies **zero** unmerged paths / conflict markers remain before proceeding.
  If markers remain, retry within a bounded budget; if still unresolved, flag with a clear
  `resume conflict unresolved` reason and preserve the bundle for manual recovery.

---

## Part 3 — Lifecycle / cleanup

- On successful commit (`ExecuteCommitStageAsync`, stage 11) or task retirement/archive
  (`TaskCompletionArchive.RetireAsync`), delete `.relay/<taskId>/flagged-work.bundle` and its sidecar.
- On re-add / fresh-run detection (`DetectReAddAndArchive`, or `firstStageToRun == 1`), clear any stale
  snapshot so a genuinely fresh run does not resurrect old work.
- Add a defensive prune for orphaned snapshots (the repo currently accumulates thousands of leftover
  worktree dirs — cleanup discipline matters here).
- Re-confirm the snapshot never enters a commit: it lives under `.relay/*` (git-ignored) and must not be
  in the proof-artifact set.

---

## Part 4 — Enable the Resume button (folded in, required for the feature to be reachable)

**Bug.** `ResumeSelectedCommand` and `RunSelectedCommand` share the same
`CanExecute = nameof(CanRunSelected)` predicate (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs`).
But the observable properties that gate it — `SelectedTask`, `IsBusy`, `PauseRequested`
(`MainWindowViewModel.cs`), and the Hugging-Face-gate flag (`MainWindowViewModel.Keys.cs`) — carry only
`[NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]`, never for `ResumeSelectedCommand`. So Resume
never re-evaluates its enabled state when a task is selected and stays stuck at its initial (no task
selected → `false`) value; the only place it is refreshed today is after a rewrite
(`MainWindowViewModel.Rewrite.cs`). That is why "Run selected" enables but "Resume" stays greyed.

**Fix.** Add `[NotifyCanExecuteChangedFor(nameof(ResumeSelectedCommand))]` next to every existing
`RunSelectedCommand` notify attribute on those observable properties, so Resume tracks the same enabled
state as Run selected. Add a test asserting `ResumeSelectedCommand.CanExecute` becomes `true` when a
non-archived task is selected (mirror any existing `RunSelected` enablement test).

---

## Constraints & done criteria

- No agent commits and no hook bypass by agents; **all** git writes here are harness-side plumbing via
  `GitInvoker`.
- Keep every new/edited `*.cs`/`*.axaml` file ≤ 300 lines; no deleting/skipping/weakening tests.
- The snapshot file must be git-ignored and absent from every commit.
- Tests (use the existing `RelayDriver` test seams):
  - flagging a mid-pipeline task creates `.relay/<taskId>/flagged-work.bundle`, and `git status` stays
    clean (it is ignored, not committed);
  - resume restores the work onto an **advanced** base (feature files reappear) and re-runs the flagged
    stage;
  - resume with an overlapping upstream change produces conflicts that the resolver step drives to zero
    markers before proceeding;
  - the bundle is deleted on successful commit / retirement.
- Manual verification: flag a task, confirm the bundle exists and is ignored, advance HEAD with an
  unrelated commit, click Resume, confirm the work is re-applied and the flagged stage re-runs.

## Files likely in scope (the plan stage will finalize the manifest)

- `src/VisualRelay.Core/Execution/RelayDriver.Events.cs` — snapshot inside/adjacent to `FlagAsync`
- a new snapshot/store helper, e.g. `src/VisualRelay.Core/Execution/FlaggedWorkStore.cs`
- `src/VisualRelay.Core/Execution/RelayDriver.cs` / `RelayDriver.Resume.cs` — the restore + resolve step
- `src/VisualRelay.Core/Execution/WorktreeResetter.cs` — ensure snapshot precedes reset (if integrated here)
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` and `MainWindowViewModel.Keys.cs` — button wiring
- tests under `tests/VisualRelay.Tests/`
