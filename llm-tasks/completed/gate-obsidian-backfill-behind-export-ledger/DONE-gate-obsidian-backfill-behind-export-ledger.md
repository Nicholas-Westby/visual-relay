## Task: Gate Obsidian summary back-fill behind a per-repo export ledger

The bridge's reconcile pass (`ReconcileExportsAsync`,
`MainWindowViewModel.ObsidianBridge.cs:143-162`) writes a vault note for
every completed task whose note is missing, keyed only on
`File.Exists(SummaryPath(id, date))`. That single dedupe rule produces
three confirmed defects:

1. **Fabricated notes for tasks Visual Relay never ran.** A completed task
   with no `.relay/<id>/stage*.report.json` yields an empty
   `TaskRunMetric`, so the note claims `$0.00` cost, `0s` duration, status
   `committed` (the `ResolveStatus` default with a null outcome and no
   status record) and `vr-completed-at` = the scan moment (tier-3 `nowUtc`
   fallback, `ObsidianSummaryWriter.cs:250`).
2. **Daily duplicates of those notes.** With no stage metrics, the
   date-folder for both the existence check
   (`MainWindowViewModel.ObsidianBridge.cs:153-155`) and the write resolves
   to "today" — so each new UTC day the check misses and a fresh copy lands
   in the new date folder, for as long as the task stays in the top-50 of
   `ListCompletedAsync`.
3. **Deleted notes resurrect.** The operator deliberately deletes notes
   from the vault; for any top-50 completed task the next idle scan
   (~poll interval) re-creates the note — and the re-created copy is
   degraded (null outcome → empty `vr-commit`, no flag reason).

### Evidence (verified)

- 2026-07-17, two independent instances fabricated the same five notes for
  the manually-archived test-speedup set (completed outside the pipeline by
  hand, commit 9bb02d9, 2026-07-09; zero `.relay/<id>` artifacts exist in
  git history): one instance at 07:42:44Z, another at 21:04:04Z into a
  second vault. Signature in every one: `committed · $0.00 · 0s`,
  `vr-completed-at` = scan time, empty `vr-commit`/`vr-source-guid`.
- The back-fill is LOAD-BEARING and must not be removed: drains run on a
  different machine than the vault-owning app, so the vault owner's
  completion-time export (`ExportSummaryOnCompletion`) never fires for
  drained tasks. Its vault is populated exclusively by this reconcile pass
  reading the committed stage reports after a git sync. A single first
  scan against an empty vault correctly produced the full
  `Completed/<date>/` tree for all metric-having tasks (observed
  2026-07-17 21:04Z) — that behavior must survive.
- Operator requirement: manually deleted notes must STAY deleted. The
  current file-existence dedupe cannot distinguish "never exported" from
  "deleted on purpose".

### What to build

1. **Export ledger.** A hidden per-repo ledger file inside the vault repo
   folder (dot-prefixed so Obsidian ignores it, e.g.
   `<vault>/<repo>/.vr-export-ledger.json`) recording the task ids that
   have ever had a summary exported. Both export paths append on every
   successful write: `ExportSummaryOnCompletion` and the reconcile
   back-fill. Atomic replace on save (temp file + rename).
2. **Back-fill gate.** Reconcile writes a note only when the task id is
   NOT in the ledger AND the task has at least one stage report
   (`metric.Stages.Count > 0`). Tasks with no run record are never
   back-filled — nothing is fabricated (fixes defect 1) and no date
   arithmetic ever falls back to "now" (fixes defect 2). A note whose id
   is in the ledger is never re-created, so deletions stick (fixes
   defect 3).
3. **First-scan seeding.** When the ledger file is absent:
   - vault repo folder has NO existing `Completed/**/*.md` notes → fresh
     vault: back-fill every metric-having completed task once (today's
     load-bearing population), recording each in the new ledger;
   - existing notes found → pre-ledger vault: seed the ledger with EVERY
     currently-completed task id without writing a single note. History is
     settled; only tasks that complete after seeding get exported.
   An unreadable/corrupt ledger is treated as absent.

### Constraints

- Do not change note content or the completion-time export behavior for
  tasks that ran. Do not touch the importer or scaffold.
- Egress guards stay: task-id validation before any vault path is
  composed; the ledger lives at a fixed name, never composed from task
  ids.
- Repo-agnostic; keep files under the 300-line guard; TimeProvider (no
  real-time waits) in tests.

### Tests (red first)

- Completed task with no `.relay/<id>`: scan writes no note, ledger gains
  nothing — fails today (fabricated `$0.00 · 0s · committed` note).
- Metric-less completed task, scans on two consecutive UTC days
  (TimeProvider): zero notes both days — fails today (one copy per day).
- Metric-having task whose note was deleted after export (id in ledger):
  scan does not re-create it — fails today (resurrection).
- Fresh vault, no ledger, no notes: one scan back-fills all metric-having
  completed tasks at their metric-derived dates and writes the ledger —
  pins the load-bearing population path.
- Vault with notes but no ledger: scan writes no new notes and seeds the
  ledger with all completed ids — fails today (missing notes get
  back-filled).
- Completion-time export records its task id in the ledger.
- Corrupt ledger file: treated as absent, seeding rules apply, no crash.

### Verification

- `./visual-relay check` fully green including the new tests.
- Manual: scratch repo + scratch vault. (1) Fresh vault scan populates
  notes and the ledger. (2) Delete one note, rescan: stays deleted.
  (3) Archive a task by hand with no `.relay/<id>`, rescan across a
  simulated day change: no note ever appears. (4) Delete the ledger,
  rescan: no new notes, ledger reseeded.
