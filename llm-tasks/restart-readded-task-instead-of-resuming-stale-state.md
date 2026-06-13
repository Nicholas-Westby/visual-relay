# A re-added task with completed .relay state silently retires on resume instead of running the new work

Twice on 2026-06-12 a target repo re-added a task under a previously
completed id (JobFinder: `fix-timing-estimates`, `remote-phrase-review`,
re-added by the repo's auto-generators after the morning drain archived
them; it happened again the same evening when the generator recreated
`fix-timing-estimates/`). The runner resumes from `.relay/<task>/status.json`
(`RelayDriverOptions.Resume: true` is hard-wired in both DrainQueue's
ConsoleTaskRunner and the headless drain path), so a re-added task whose
prior run left all 11 stages Done is treated as already complete: the run
no-ops straight to retirement and the NEW task content is never executed —
silent work loss, indistinguishable from success in the drain summary.

The 2026-06-12 morning drain only did the right thing because the operator
manually archived the stale dirs first (`.relay/<id>` →
`.relay/<id>.stale-20260611`). That workaround is now also worse than it
was: the canonical state files (status.json, ledger.md, manifest.txt,
seals) are git-tracked in target repos, so renaming the dir dirties the
tree mid-run.

## Goal

When a pending task's markdown is newer than its completed `.relay` state,
Visual Relay runs the task fresh — re-added work executes; nothing silently
retires. A genuinely interrupted run (state not all-Done) keeps today's
resume behavior exactly.

## Approach (suggested)

- Detection: at run start, if status.json shows the terminal stage Done
  (or the task was previously retired) AND the canonical task .md's
  content differs from what the prior run executed (cheapest robust
  signal: compare the task input hash recorded in the run state — add one
  if absent — falling back to md mtime > status.json mtime), classify the
  task as *re-added*, not *resumable*.
- Action on re-added: archive the old state dir to
  `.relay/<id>.run-<runId>/` (or prune it) and start from stage 1 with a
  fresh attempt history. Preserve the old dir for forensics rather than
  overwriting attempt files in place (attempt numbering already guards
  overwrites within a run, not across retirements).
- Surface the decision in the event log (`run_start` line noting
  `fresh: prior state archived (re-added task)`), so drain logs make the
  classification auditable.
- Regression tests: (1) all-Done state + modified task md → fresh run,
  old state archived, new commit contains the new work; (2) all-Done
  state + identical md → current behavior (retire/no-op) preserved;
  (3) partial state (stages 1-7 Done) + any md → resume, never archived;
  (4) the archived dir name never collides on repeated re-adds.
