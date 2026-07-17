## Task: Stop back-filling fabricated Obsidian summaries for tasks Visual Relay never ran

The Obsidian bridge's reconcile pass (`ReconcileExportsAsync`,
`MainWindowViewModel.ObsidianBridge.cs:143-162`) back-fills a vault summary
for every completed task that has no note yet. For a task with no recorded
run — no `.relay/<id>/stage*-attempt*.report.json` at all — it still writes
a note, and every field in it is fabricated: `vr-cost-usd: $0.00` and
`vr-duration: 0s` (from the empty `TaskRunMetric`), `vr-status: committed`
(the `ResolveStatus` default when there is no outcome and no status record),
and `vr-completed-at` = the moment the scan happened to run (the
`ResolveCompletionDate` tier-3 `nowUtc` fallback,
`ObsidianSummaryWriter.cs:250`). The note reads as measured run data
("Committed · $0.00 · 0s") when in fact Visual Relay knows nothing about
the task's run.

Worse, the write is not idempotent across days. The reconcile existence
check uses `DateTime.UtcNow.Date` as the date-folder when the task has no
stage metrics (`MainWindowViewModel.ObsidianBridge.cs:153-155`), and the
writer's own date resolution falls through to `nowUtc` for the same reason
— so tomorrow the check looks in tomorrow's folder, finds nothing, and
writes a fresh copy. One bogus note per unrun completed task per day, for
as long as the task stays in the top-50 of `ListCompletedAsync`.

### Evidence (verified)

- Field occurrence, 2026-07-17: the vault note
  `Completed/2026-07-17/05-scope-down-headless-ui-and-verify-60s-ceiling.md`
  claims `committed · $0.00 · 0s · vr-completed-at 2026-07-17T07:42:44+00:00`
  with empty `vr-commit` and `vr-source-guid` — the exact reconcile
  signature (`writer.Write(layout, RootPath, task.Id, null, spec, null, …)`
  passes null outcome and null source guid). That task was actually
  completed OUTSIDE the pipeline and archived by hand on 2026-07-09
  (commit 9bb02d9, "chore: archive the completed test-speedup task set");
  no `.relay/<id>` artifact for it exists anywhere in git history. The same
  applies to the other four tasks in that set (01-remove-static-asset-tests,
  02-in-memory-git-simulator, 03-migrate-git-tests-to-simulator,
  04-eliminate-real-time-waits-in-tests) — all five received fabricated
  notes dated 2026-07-17.
- The report that surfaced this compared that note against the archive card
  for `05-reject-overlong-commit-subjects-instead-of-truncating` ($0.46) —
  a different task that shares the "05-" ordinal from a different batch. No
  measured cost was lost or dropped; do not chase a metrics-loss bug. The
  defect is that the back-fill fabricates data and duplicates daily.
- `ReconcileExportsAsync` shipped with the original bridge commit d2a0040
  and has always had this behavior. The completion-time export path
  (`ExportSummaryOnCompletion`) is fine: it runs with a real outcome and
  real metrics.
- `CompletionTimeResolver` (`src/VisualRelay.Core/Tasks/`) already
  implements the stable completion-date chain (metrics → `.relay` mtime →
  git committer date → markdown mtime) that the reconcile date logic lacks.

### What to build

1. **Skip unrun tasks**: the reconcile back-fill must not write a note for
   a completed task with no recorded run (`metric.Stages.Count == 0`).
   Back-fill exists to catch pipeline-run tasks whose completion-time
   export was missed (app crash, bridge disabled at the time); it must not
   invent summaries for work done outside Visual Relay.
2. **Stable dates, one source**: the date-folder used by the reconcile
   existence check and the date the writer resolves must come from one
   shared computation, and for tasks with stage reports it must stay the
   metric-derived date (regression-pin this — it already behaves correctly
   today). "Now" must never leak into a completed task's date folder or
   `vr-completed-at` via the back-fill path.
3. **Clean up prior fabrications**: during a reconcile scan, remove a note
   only when ALL of these hold: its `vr-task-id` matches a completed task
   with no recorded run, AND its frontmatter carries the full fabricated
   signature (`vr-cost-usd: $0.00`, `vr-duration: 0s`, empty `vr-commit`,
   empty `vr-source-guid`). Notes that fail any part of the check
   (user-edited notes, tasks that have run records, real $0 runs with a
   commit sha) must never be touched. Deletion is bounded to
   `Completed/<date>/<task-id>.md` paths the writer itself would compose.

### Constraints

- Do not change `ExportSummaryOnCompletion` or the summary content for
  tasks that DID run. Do not touch the importer.
- Egress guards stay: task-id validation before any vault path is composed.
- Repo-agnostic; keep files under the 300-line guard; TimeProvider (no
  real-time waits) in tests.

### Tests (red first)

- Completed task with no `.relay/<id>` dir: a reconcile scan writes no
  note — fails today (it writes a `$0.00 · 0s · committed` note dated
  "now").
- Same setup with an already-fabricated note present from a "previous day":
  the scan removes it — fails today.
- Fabricated-signature note whose task DOES have stage reports: untouched.
- Note whose frontmatter deviates from the signature in any field
  (e.g. non-empty `vr-commit`, edited cost): untouched.
- Completed task WITH stage reports and no note: back-fill still writes it,
  at the metric-derived date, with the real cost/duration (regression pin).
- Two scans across a simulated UTC day boundary for a metric-having task:
  exactly one note, in the metric-derived date folder (regression pin).

### Verification

- `./visual-relay check` fully green including the new tests.
- Manual: in a scratch repo, archive a task by hand (no `.relay/<id>`),
  enable the bridge, run two scans across a simulated day change: no note
  appears, and a planted fabricated note is removed. Complete a pipeline
  task with the bridge disabled, re-enable, scan: its note back-fills with
  real metrics.
