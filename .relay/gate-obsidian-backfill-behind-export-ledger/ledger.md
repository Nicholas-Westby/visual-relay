## Stage 1 - Ideate

{
  "summary": "Gate the Obsidian back-fill (ReconcileExportsAsync) behind a per-repo export ledger (<vault>/<repo>/.vr-export-ledger.json) to fix three defects: fabricated $0.00/0s notes for tasks without stage reports, daily duplicates of those notes due to date-fallback to 'today', and resurrected notes after manual deletion. The ledger records every exported task id; reconcile writes only when id ∉ ledger AND metric.Stages.Count > 0. Completion-time export (ExportSummaryOnCompletion) also records in the ledger. First-scan seeding: if no ledger and no Completed/**/*.md notes → full back-fill; if no ledger but notes exist → seed ledger without writing. A corrupt/missing ledger is treated as absent. Writer unchanged. Egress guards preserved.",
  "options": [
    "Option A: New ExportLedger helper class in ObsidianBridge/ with ContainsAsync/RecordAsync/RecordBatchAsync/TrySeedAsync. Gate logic in ReconcileExportsAsync (check ledger + metric.Stages.Count > 0) and ExportSummaryOnCompletion (record after write). First-scan detection in ExportLedger.TrySeedAsync which scans Completed/**/*.md to decide fresh-vs-pre-ledger. Minimal surface-area change; writer stays pure.",
    "Option B: Make ObsidianSummaryWriter ledger-aware by adding ShouldExport/RecordExport methods or a WriteWithLedger overload that internally checks the ledger and stage count. First-scan seeding as a static method. Single chokepoint for export decisions but risks breaking the 300-line guard and blurs the writer's single responsibility.",
    "Option C: New ObsidianExportCoordinator service composing ExportLedger + ObsidianSummaryWriter. Both call sites delegate to the coordinator. Cleanest abstraction with the thinnest view model, easiest to test in isolation, but introduces more new files and re-routes both export paths through a new indirection layer."
  ]
}

## Stage 2 - Research

{
  "findings": "Codebase investigation complete. The bridge reconcile pass (MainWindowViewModel.ObsidianBridge.cs:143-162) iterates top-50 completed tasks, reads a TaskRunMetric via RelayRunHistory.ReadTaskMetric, computes a date (stages max or UtcNow.Date fallback), and writes a note if File.Exists(SummaryPath) is false. The single File.Exists dedupe causes three defects: (1) tasks with no .relay/<id> artifacts produce an empty metric (CostUsd=0, DurationSeconds=0) with ResolveStatus defaulting to 'committed' and completion date falling back to scan-time UtcNow; (2) that date fallback means each new UTC day the check misses and a fresh degraded copy lands; (3) manually deleted notes are re-created because File.Exists returns false after deletion. ExportSummaryOnCompletion (line 167) fires after single-run and drain completion, writing to vault unconditionally (no ledger exists yet). The solution requires: (A) new ExportLedger class in src/VisualRelay.Core/ObsidianBridge/ with ledger file path = ObsidianVaultLayout.RepoDir/.vr-export-ledger.json, methods ContainsAsync/RecordAsync/RecordBatchAsync/TrySeedAsync, atomic write via temp+rename; (B) modify ReconcileExportsAsync to gate on id not-in-ledger AND metric.Stages.Count > 0; (C) modify ExportSummaryOnCompletion to record in ledger after write; (D) first-scan seeding in TrySeedAsync — no ledger + no Completed/**/*.md notes = full back-fill with ledger write, no ledger + existing notes = seed ledger silently without writing. The writer (ObsidianSummaryWriter) stays pure/unchanged. Egress guard (IsValidTaskId) must apply before any ledger path composition. The 300-line guard applies: ObsidianBridge.cs is 222 lines (new logic may push it near the limit or require extracting helper methods). Tests use ManualTimeProvider for virtual time, TestRepository for temp repos, [Collection('Headless')] and AvaloniaFact for Avalonia headless tests, and event-driven TestWaits.ForFileAsync for file detection.",
  "constraints": [
    "300-line guard per file — none of the modified files may exceed 300 lines.",
    "Do not change note content or the completion-time export behavior for tasks that ran.",
    "Do not touch the importer (ObsidianTaskImporter) or scaffold (EnsureScaffold).",
    "Egress guards stay: IsValidTaskId must be checked before composing any vault path (including ledger path).",
    "The ledger file name is fixed (.vr-export-ledger.json), never composed from task ids.",
    "Repo-agnostic: the ledger lives under <vault>/<repo>/ (per repo), composed via ObsidianVaultLayout.RepoDir.",
    "TimeProvider (ManualTimeProvider) in tests — no real-time waits or DateTime.UtcNow in test assertions.",
    "Atomic file replace on ledger save (temp file + rename) — never partial/corrupt writes.",
    "An unreadable/corrupt ledger file is treated as absent (seeding rules apply, no crash).",
    "The back-fill is load-bearing for drain-completed tasks — must not be removed, only gated.",
    "All existing tests must remain green.",
    "New test scenarios (red first): no-stage metric task writes no note, metric-less across two UTC days writes zero notes, metric-having deleted-after-export does not resurrect, fresh vault back-fills all at metric dates, pre-ledger vault seeds without writing, completion-time export records in ledger, corrupt ledger treated as absent."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The bridge reconcile pass (MainWindowViewModel.ObsidianBridge.cs:143-162) deduplicates solely on File.Exists(layout.SummaryPath(task.Id, date)) at line 157. Each invocation copies 11 lines of date-resolution logic already present inside ObsidianSummaryWriter (lines 218-261), diverging at line 155 where a zero-stage metric falls to DateOnly.FromDateTime(DateTime.UtcNow.Date) — the writer's own ResolveCompletionDate already does a three-tier resolution but the reconcile loop pre-dates and shadows it. The call at line 160 passes null outcome and DateTimeOffset.UtcNow, so (a) ResolveStatus returns 'committed' (default when outcome is null and status record is empty, line 207), and (b) nowUtc alone cannot override the already-computed date because Write computes its own date via ResolveCompletionDateOnly — but the existence-check date at line 157 is computed independently and mismatches Write's internal date when stages are empty (both land on 'today' but Write's tier-3 fallback can differ from UtcNow.Date by a sub-day offset).\n\nExportSummaryOnCompletion (line 167) writes unconditionally via new ObsidianSummaryWriter().Write(...), never recording to any ledger. The drain lifecycle hook calls it fire-and-forget at LiveState.cs:62; the single-run path calls it awaited at RunOne.cs:32.\n\nThree defect chains:\n\n1. Fabricated notes: No .relay/<id> artifacts → ReadTaskMetric returns TaskRunMetric(taskId, []) with CostUsd=0, DurationSeconds=0, Stages.Count=0. The reconcile date-fallback (line 155) puts it in today's folder. Write is called with null outcome, so ResolveStatus returns 'committed' (null → no Status check → line 207 default). The note shows $0.00, 0s, committed, vr-completed-at = scan moment, empty vr-commit, empty vr-source-guid.\n\n2. Daily duplicates: With Stages.Count=0, the date for both the existence check (line 153-155) and Write resolves to 'today'. Each new UTC day: File.Exists(SummaryPath(id, \"2026-07-17\")) is true, File.Exists(SummaryPath(id, \"2026-07-18\")) is false → fresh degraded copy lands in the new date folder. Repeats for as long as the task stays in top-50 of ListCompletedAsync.\n\n3. Deleted notes resurrect: Operator deletes Completed/<date>/<id>.md. Next scan: File.Exists returns false → note is re-created. The re-created copy is degraded (null outcome → empty vr-commit, no flag reason, incomplete frontmatter). Never stops as long as task is in top-50.\n\nNo ExportLedger class exists anywhere in the codebase. No .vr-export-ledger.json file or concept of a per-repo export journal is present.\n\nLine-count headroom: ObsidianBridge.cs is 222 lines (78 under the 300-line guard). A new ExportLedger.cs in ObsidianBridge/ would be a net-new file. ObsidianSummaryWriter.cs is 272 lines (28 under) and must stay unchanged per constraints. ObsidianVaultLayout.cs is 217 lines (83 under).",

  "excerpts": [
    "MainWindowViewModel.ObsidianBridge.cs:143-162 — ReconcileExportsAsync loop:\n\nvar metric = RelayRunHistory.ReadTaskMetric(RootPath, task.Id);\nvar date = metric.Stages.Count > 0\n    ? DateOnly.FromDateTime(metric.Stages.Max(s => s.Timestamp).Date)\n    : DateOnly.FromDateTime(DateTime.UtcNow.Date);  // ← defect 2: 'today' fallback\n\nif (File.Exists(layout.SummaryPath(task.Id, date))) continue;  // ← defect 3: no ledger, only filesystem dedupe\n\nvar spec = await File.ReadAllTextAsync(task.MarkdownPath);\nwriter.Write(layout, RootPath, task.Id, null, spec, null, DateTimeOffset.UtcNow);  // ← defect 1: null outcome",

    "MainWindowViewModel.LiveState.cs:62 — Drain fire-and-forget: _ = ExportSummaryOnCompletion(taskId, outcome);  ← no ledger record after",

    "MainWindowViewModel.RunOne.cs:32 — Single-run path: await ExportSummaryOnCompletion(task.Id, outcome);  ← no ledger record after",

    "ObsidianSummaryWriter.cs:178-208 — ResolveStatus: when outcome is null AND statusEntries is empty (no .relay/<id>/status.json), returns 'committed' at line 207. This is the correct default for a null-outcome inference chain but produces misleading 'committed' for never-run tasks.",

    "ObsidianSummaryWriter.cs:218-251 — ResolveCompletionDate three-tier fallback: (1) max stage Timestamp, (2) newest file mtime in .relay/<id>/, (3) nowUtc. Tier 3 fires for no-stage tasks with no .relay dir → scan moment.",

    "RelayRunHistory.cs:13-30 — ReadTaskMetric: when .relay/<id> directory doesn't exist (line 16-18), returns new TaskRunMetric(taskId, []) with zero stages, zero cost, zero duration.",

    "ObsidianVaultLayout.cs:71-72 — SummaryPath: Path.Combine(CompletedDir(date), $\"{taskId}.md\"). The dated folder is the only namespace — no ledger oracle.",

    "ExportSummaryOnCompletion (ObsidianBridge.cs:167-179) — Writes unconditionally. Constructs a fresh ObsidianSummaryWriter each time (stateless). No post-write record-keeping."
  ],

  "repro": "To reproduce defect 1: (a) Archive a task by hand by writing DONE-<id>.md to llm-tasks/completed/batch-001/ with no .relay/<id>/ directory. (b) Enable the bridge and run RunObsidianBridgeScanAsync. (c) Observe Completed/<today>/<id>.md appears with vr-status: committed, vr-cost-usd: $0.00, vr-duration: 0s, empty vr-commit, empty vr-source-guid, and vr-completed-at = scan time. \n\nTo reproduce defect 2: (d) Using a ManualTimeProvider, advance time by 24h and run another scan. (e) Observe Completed/<tomorrow>/<id>.md appears — two degraded notes now exist for the same never-run task.\n\nTo reproduce defect 3: (f) Create a task with stage reports, run a scan to produce a note, confirm the note exists. (g) Delete the note from the vault. (h) Run another scan — the note reappears (degraded, since no outcome is available from the reconcile path)."
}

## Stage 4 - Plan

{
  "plan": "Add ExportLedger class (src/VisualRelay.Core/ObsidianBridge/ExportLedger.cs) recording exported task ids in <vault>/<repo>/.vr-export-ledger.json with atomic temp+rename saves. Gate ReconcileExportsAsync (MainWindowViewModel.ObsidianBridge.cs:143-162) behind ledger.ContainsAsync AND metric.Stages.Count > 0, replacing the old File.Exists dedupe. Record after every successful export in both reconcile and ExportSummaryOnCompletion paths. First-scan seeding via TrySeedAsync: no ledger + no Completed/**/*.md notes → FullBackfill (back-fill all metric-having tasks); no ledger + existing notes → SealOnly (seed ledger silently, write nothing); valid ledger → Skip (normal top-50 gated loop). Corrupt ledger treated as absent. Writer unchanged. Two new test files: ObsidianExportLedgerTests.cs (plain Facts for ExportLedger unit tests) and ObsidianBridgeExportGateTests.cs (AvaloniaFact VM integration tests covering all 7 scenarios). All files under 300 lines.",
  "manifest": [
    "+src/VisualRelay.Core/ObsidianBridge/ExportLedger.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs",
    "+tests/VisualRelay.Tests/ObsidianExportLedgerTests.cs",
    "+tests/VisualRelay.Tests/ObsidianBridgeExportGateTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded untracked deleted: 1.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/ObsidianExportLedgerTests.cs",
    "tests/VisualRelay.Tests/ObsidianBridgeExportGateTests.cs"
  ],
  "rationale": "Both test files compile cleanly and all 15/19 tests correctly fail against the current codebase. The ExportLedger stub returns safe defaults (ContainsAsync→false, RecordAsync→no-op, TrySeedAsync→Skip), causing 8 unit-test failures on storage/retrieval/seeding behavior. The 7 VM integration tests exercise the real ReconcileExportsAsync which still uses File.Exists dedupe — they fail exactly as the task predicts: fabricated $0.00 notes for never-run tasks (defect 1), daily duplicates from date-fallback (defect 2 observable), and deleted-note resurrection (defect 3). The 4 passing ExportLedger tests validate only the stub's safe defaults (unknown-id returns false, empty batch doesn't corrupt, corrupt ledger treated as empty)."
}

## Stage 6 - Implement

{ "summary": "Implemented ExportLedger class (139 lines) in src/VisualRelay.Core/ObsidianBridge/ with atomic temp+rename persistence, SeedDecision enum (FullBackfill/SealOnly/Skip), ContainsAsync, RecordAsync, RecordBatchAsync, and TrySeedAsync. Modified ReconcileExportsAsync (270 lines, up from 222) to gate writes behind ledger.ContainsAsync AND metric.Stages.Count > 0, with first-scan seeding logic using HasCompletedNotes that checks for task notes (excluding ReservedFileNames) and dated subdirectories. Added ledger.RecordAsync to ExportSummaryOnCompletion. Fixed test helper AnyCompletedNote to exclude ReservedFileNames. All 19 new tests pass (12 unit + 7 integration), full suite 3183 passed / 0 failed. All files under 300-line guard." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Pure backend C# logic change (ExportLedger class, gate conditions in reconcile loop, ledger recording in completion export). No UI markup, styles, layouts, images, charts, terminal UI, or rendered documents are touched. Note content and templates are unchanged per constraints._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Gate Obsidian back-fill behind per-repo export ledger. New ExportLedger class (139 lines) records exported task IDs with atomic temp+rename persistence. ReconcileExportsAsync writes only when id not in ledger AND metric.Stages.Count > 0 — fixes fabricated $0.00/0s notes for never-run tasks, daily duplicates from date fallback, and deleted note resurrection. ExportSummaryOnCompletion records in ledger. First-scan seeding: fresh vault → full back-fill; pre-ledger vault → seed ledger silently. Corrupt ledger treated as absent. 2 new test files (19 tests). All 3183 tests pass.",
  "commitMessages": [
    "feat(obsidian): gate back-fill behind export ledger",
    "fix: stop resurrecting deleted vault notes on reconcile",
    "fix(obsidian): prevent daily duplicate export notes",
    "feat: add per-repo export ledger with first-scan seeding",
    "fix(obsidian): block fabricated notes for unrun tasks"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

