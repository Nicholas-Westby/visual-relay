## Task: Capture the untracked baseline when a resume-mode run has no snapshot

Every GUI-driven run executes with `Resume: true`
(`src/VisualRelay.App/ViewModels/GuiTaskRunner.cs:26`). For a task that has
never run, `CapturePreRunUntrackedAsync`
(`src/VisualRelay.Core/Execution/RelayDriver.Snapshot.cs:56-63`) then takes the
resume branch, finds no `pre-run-untracked.txt`, and silently falls back to an
**empty set** — and writes no snapshot file. From that moment the run believes
the repository contained zero untracked files at start, so **every pre-existing
untracked file is misattributed as "authored by this run"**.

Contrast with `CaptureRunBaseShaAsync` in the same file (lines 83-109): when the
persisted `run-base.txt` is missing on resume it falls through to capturing the
current HEAD and persisting it. The untracked snapshot must behave the same
way. The asymmetry is the bug.

### Evidence (2026-07-15 drain, all verified)

- All three `.relay/<task>/` dirs from the drain (`hoist-pipeline-test-shared-setup`,
  `merge-nocommit-contamination-tests-data-driven`, `split-key-setup-panel-ui-tests`)
  contain `run-base.txt` but **no `pre-run-untracked.txt`** — the exact
  signature of the empty-set branch (the fresh branch at Snapshot.cs:66-67
  always writes the file; nothing in src deletes it).
- Consequence 1 (false flags): with an empty baseline, the commit gate
  (`RelayDriver.CommitGate.cs:216-227`) treats every leftover untracked file as
  authored. A user attached a screenshot to a *queued* task's folder at 21:38
  while the drain was running; the merge task (22:03) and split task (22:29)
  each flagged `sealed commit is missing authored files` on that file. (It was
  only exposed because the non-ASCII filename also defeats the llm-tasks
  exemption — see the companion task
  `01-unquote-git-paths-for-non-ascii-filenames` — but the misattribution itself
  is this bug: with a correct baseline the file would have been excluded for
  the split run regardless of quoting.)
- Consequence 2 (data-loss hazard): after a flag, `WorktreeResetter.ResetAsync`
  reads the same missing snapshot (`WorktreeResetter.cs:30-33`), gets an empty
  set, and **deletes every untracked non-internal, non-tasksDir file in the
  repo** — including files that existed before the run and were never touched
  by the agent. Any user scratch file outside `llm-tasks/` sitting in the repo
  during a flagged run is silently deleted (it is captured in
  `flagged-work.bundle` first, which softens but does not excuse it). The
  screenshot escaped deletion only because the quoting bug made `File.Exists`
  false.
- Consequence 3 (dishonest log): the drain log records `reset-removed …
  1 untracked file(s): …` for a file that was never actually deleted, because
  the resetter reports its intent list, not verified deletions
  (`WorktreeResetter.cs:47-52` vs `RelayQueueController.PrivateHelpers.cs:28-34`).

### What to build

1. In `CapturePreRunUntrackedAsync`, replace the resume/no-file empty-set
   fallback with a fresh capture + persist (mirroring `CaptureRunBaseShaAsync`).
   Preserve genuine resume semantics: when the file exists, keep using it.
   Consider whether `forceFresh` (re-added tasks) still needs to exist after
   this change.
2. Make `WorktreeResetter.ResetAsync` return only files it actually deleted;
   if a listed file could not be deleted (or did not exist), report that
   separately and loudly (a distinct drain-log event, not silence).
3. Decide and document the correct behavior when the snapshot file is missing
   at reset time (e.g. refuse to delete anything rather than delete
   everything) — deleting on an unknown baseline is the dangerous default.

### Constraints

- Repo-agnostic; no assumptions that untracked files live under any particular
  directory.
- Do not weaken the commit gate itself — the gate's job (catching genuinely
  lost authored files) stays; only the baseline it compares against is fixed.
- Keep files under the 300-line guard.

### Tests (red first)

- Resume-mode run of a never-run task in a repo with a pre-existing untracked
  file: the file must appear in the captured baseline, `pre-run-untracked.txt`
  must be written, the commit gate must not report it, and a post-flag reset
  must not delete it.
- Resume-mode run of a task WITH an existing snapshot: persisted snapshot is
  reused unchanged (current behavior preserved).
- Resetter honesty: when a file in the delete list cannot be deleted, the
  returned removed-list excludes it and a failure is surfaced.

### Verification

- `./visual-relay check` fully green including the new tests.
