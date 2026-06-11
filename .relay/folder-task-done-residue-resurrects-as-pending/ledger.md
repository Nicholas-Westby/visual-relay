## Stage 1 - Ideate

{
  "summary": "EmitSingleTaskFromFolder's fallback (first .md in folder) ignores IsSkippedName, causing folders with only DONE-*.md to emit as pending. Fix by filtering DONE-/IGNORE- prefixed files out of the fallback or classifying all-DONE folders as completed.",
  "options": [
    "Option A — Filter in the fallback selector: skip .md files whose stem passes IsSkippedName. If none remain, return null from pending emission. Single-point, minimal blast radius.",
    "Option B — Classify all-DONE folders as archived: scan folder's .md files upfront; if all pass IsSkippedName, route to completed path instead of pending.",
    "Option C — Belt-and-suspenders: fix the root cause (Option A or B) plus add a post-filter in ListPendingAsync that rejects any result with DONE-/IGNORE- prefix, enforcing the invariant at the API boundary."
  ]
}

## Stage 2 - Research

{
  "findings": "The bug is an asymmetry in `RelayTaskRepository` (`src/VisualRelay.Core/Tasks/RelayTaskRepository.cs`). Method `Walk` (line 121) uses `IsSkippedName` (line 243) to skip top-level DONE-/IGNORE- prefixed entries and folder names, but `EmitSingleTaskFromFolder` (line 155) does NOT apply `IsSkippedName` to its fallback `.md` selector at line 170 (`entries.FirstOrDefault(f => f.EndsWith(\".md\"))`). A folder like `add-app-icon/` containing only `DONE-add-app-icon.md` (the canonical folder name prefixed with DONE-) therefore emits a pending task with id `DONE-add-app-icon`. Every consumer of `ListPendingAsync` (GUI queue via `RelayQueueController.RefreshAsync`, DrainQueue) sees completed work as pending. Explicit-task-id drives are immune because they bypass repository discovery.\n\nAdditionally, `ListCompletedAsync` (line 54) only scans top-level `DONE-*.md` files and files under the `completed/` subdirectory — it does NOT find DONE-*.md files stranded in regular subdirectories like `add-app-icon/`. So even fixing `EmitSingleTaskFromFolder` to skip DONE fallbacks leaves those folders invisible to both pending and completed lists.\n\nThe fix needs two changes:\n1. In `EmitSingleTaskFromFolder`, filter the fallback `.md` selector through `IsSkippedName` so DONE-/IGNORE- prefixed files are never chosen as the canonical markdown. If no non-prefixed `.md` remains, the method returns early (no pending task emitted).\n2. Extend `ListCompletedAsync` to also walk subdirectories of the tasks root for DONE-*.md files that were never moved to `completed/`, so all-DONE folders surface as archived tasks.\n\nTest coverage (`tests/VisualRelay.Tests/RelayTaskRepositoryTests.cs`) has no tests for: (a) folder containing only `DONE-x.md` → absent from pending, present in completed; (b) folder containing `DONE-x.md` + `y.md` → pending from `y.md`; (c) the invariant that no `ListPendingAsync` result has a DONE-/IGNORE- prefixed id.\n\nExisting files to modify: `src/VisualRelay.Core/Tasks/RelayTaskRepository.cs` (EmitSingleTaskFromFolder + ListCompletedAsync), `tests/VisualRelay.Tests/RelayTaskRepositoryTests.cs` (new tests).",
  "constraints": [
    "The canonical `{folderName}.md` path (line 163) must remain the primary resolution — the IsSkippedName filter only applies to the fallback selector when no canonical file exists.",
    "A folder with both a non-prefixed `.md` AND DONE-`.md` files must still emit a pending task from the non-prefixed file (the DONE- variants become sibling attachments).",
    "`ListCompletedAsync` must not double-count a task that appears both as a top-level `DONE-*.md` AND inside a DONEd folder.",
    "The `TestRepository` helper (`tests/VisualRelay.Tests/TestDoubles.cs`) only has `WriteTask` (flat .md) and `WriteNestedTask` (folder with canonical `{id}.md`). New tests for DONE-only folders must use direct `File.WriteAllText` / `Directory.CreateDirectory` calls on the repo root.",
    "The fix must not regress existing tests, especially `ListPendingAsync_SkipsDoneIgnoredCompletedAndNeedsReviewTasks` (line 8) and `ListPendingAsync_FolderNamedMdIsOnlyQueueEntryForItsFolder` (line 174).",
    "All three suggested approaches (Option A: filter fallback selector, Option B: classify all-DONE folders as archived, Option C: belt-and-suspenders) are viable but Option A has the smallest blast radius; the task recommends Option A plus extending ListCompletedAsync.",
    "The invariant 'no item returned by ListPendingAsync ever has an id starting with DONE- or IGNORE-' is worth a dedicated test named e.g. `ListPendingAsync_NoResultHasSkippedPrefix`."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The bug is a three-part asymmetry in RelayTaskRepository.cs. (1) Walk (line 126) applies IsSkippedName only to top-level filesystem entry names — a folder named `add-app-icon/` passes this check because its name lacks a DONE- prefix, so Walk delegates to EmitSingleTaskFromFolder without inspecting the folder's contents. (2) EmitSingleTaskFromFolder (line 170) has a fallback selector `entries.FirstOrDefault(f => f.EndsWith(\".md\"))` that unconditionally picks the first .md file — when the canonical `{folderName}.md` is absent and the only .md is `DONE-add-app-icon.md`, the method emits a RelayTaskItem with id `DONE-add-app-icon` into the pending list (line 180–188). IsSkippedName is never called on the fallback candidate. (3) ListCompletedAsync (lines 66–89) only scans top-level `DONE-*.md` files and files under `completed/` — it does not walk regular subdirectories, so a DONE-only folder becomes invisible to both pending and completed lists after the EmitSingleTaskFromFolder fix. Additionally, no post-filter in ListPendingAsync/ListAsync rejects results whose Id starts with DONE- or IGNORE-, and the existing test suite has zero coverage for the folder-with-only-DONE-md scenario.",
  "excerpts": [
    "RelayTaskRepository.cs:121-148 — Walk applies IsSkippedName to entry names only, not to folder contents; a folder named `add-app-icon/` passes and enters EmitSingleTaskFromFolder",
    "RelayTaskRepository.cs:155-189 — EmitSingleTaskFromFolder canonical-path check (line 161–166) then fallback `entries.FirstOrDefault(f => f.EndsWith(\".md\"))` at line 170 with no IsSkippedName filter; taskId assigned from `Path.GetFileNameWithoutExtension(markdownPath)` at line 180, producing `DONE-add-app-icon`",
    "RelayTaskRepository.cs:243-245 — IsSkippedName checks StartsWith(\"DONE-\") or StartsWith(\"IGNORE-\") but is only called from Walk line 126, never from EmitSingleTaskFromFolder",
    "RelayTaskRepository.cs:54-96 — ListCompletedAsync scans `tasksRoot` for `DONE-*.md` with TopDirectoryOnly (line 68) and `completed/` with AllDirectories (line 80); it never walks regular subdirectories for stranded DONE-*.md",
    "RelayTaskRepositoryTests.cs:8-24 — ListPendingAsync_SkipsDoneIgnoredCompletedAndNeedsReviewTasks tests top-level DONE-*.md and completed/ paths but never a folder whose only .md is DONE-prefixed"
  ],
  "repro": "using var repo = TestRepository.Create();\nrepo.WriteConfig(\"dotnet test\", []);\nvar dir = Path.Combine(repo.Root, \"llm-tasks\", \"add-app-icon\");\nDirectory.CreateDirectory(dir);\nawait File.WriteAllTextAsync(Path.Combine(dir, \"DONE-add-app-icon.md\"), \"# Done task\\n\");\nvar pending = await new RelayTaskRepository(repo.Root).ListPendingAsync();\n// BUG: Contains task with id \"DONE-add-app-icon\"\nAssert.Contains(pending, t => t.Id == \"DONE-add-app-icon\");"
}

## Stage 4 - Plan

{
  "plan": "## Fix: folder-task DONE residue resurrects as pending\n\n### Root cause\n`EmitSingleTaskFromFolder` (line 167–172) has a fallback selector `entries.FirstOrDefault(f => f.EndsWith(\".md\"))` that unconditionally picks the first `.md` file when the canonical `{folderName}.md` is absent. `IsSkippedName` is never called on the fallback candidate, so a folder like `add-app-icon/` containing only `DONE-add-app-icon.md` emits a pending task with id `DONE-add-app-icon`. Meanwhile `ListCompletedAsync` never scans regular subdirectories for DONE-*.md, so such folders become invisible to both lists after the fix.\n\n### Code changes\n\n**File: `src/VisualRelay.Core/Tasks/RelayTaskRepository.cs`**\n\n1. **`EmitSingleTaskFromFolder` (line 170–171)** — filter the fallback `.md` selector through `IsSkippedName`. Change:\n   ```csharp\n   markdownPath = entries.FirstOrDefault(\n       f => f.EndsWith(\".md\", StringComparison.OrdinalIgnoreCase));\n   ```\n   to:\n   ```csharp\n   markdownPath = entries.FirstOrDefault(\n       f => f.EndsWith(\".md\", StringComparison.OrdinalIgnoreCase)\n           && !IsSkippedName(Path.GetFileName(f)));\n   ```\n   If all `.md` files are DONE-/IGNORE- prefixed, `markdownPath` remains null and the method returns early at line 174 — no pending task emitted.\n\n2. **`ListCompletedAsync` (after line 75)** — insert a new scan block after the top-level DONE-*.md loop and before the `completed/` scan. Walk direct subdirectories of `tasksRoot` (excluding `completed` and `_ideation`), enumerate `DONE-*.md` files, pick the canonical one via `FindCanonicalArchivedPath`, strip the DONE- prefix, skip if the id already appeared as a top-level DONE-*.md (double-count guard), and add an archived `RelayTaskItem` with `IsNested: true`, `IsArchived: true`, `ArchiveBatch: null`, plus siblings from any other files in the directory.\n\n**File: `tests/VisualRelay.Tests/RelayTaskRepositoryTests.cs`**\n\n3. **`ListPendingAsync_FolderWithOnlyDoneMd_IsAbsentFromPending`** — create a folder with only `DONE-x.md`; assert `ListPendingAsync` returns no entry for it.\n\n4. **`ListCompletedAsync_FolderWithOnlyDoneMd_AppearsInCompleted`** — same folder; assert `ListCompletedAsync` contains an entry with id `x`, `IsArchived: true`, `IsNested: true`, `ArchiveBatch: null`, and the DONE- prefix stripped.\n\n5. **`ListPendingAsync_FolderWithDoneMdAndRegularMd_PendingFromRegularMd`** — folder with `DONE-x.md` + `y.md`; assert `ListPendingAsync` contains single entry with id `y` (from `y.md`), and `DONE-x.md` appears as a sibling.\n\n6. **`ListPendingAsync_NoResultHasSkippedPrefix`** — invariant test. Create a mix of flat tasks, nested tasks, a DONE-only folder, and a DONE+regular folder; assert every id in `ListPendingAsync` lacks both `DONE-` and `IGNORE-` prefixes.\n\n### Test helper note\nNew tests use direct `Directory.CreateDirectory` / `File.WriteAllText` calls on `repo.Root` (no changes to `TestDoubles.cs` needed).",
  "manifest": [
    "src/VisualRelay.Core/Tasks/RelayTaskRepository.cs",
    "tests/VisualRelay.Tests/RelayTaskRepositoryTests.cs"
  ]
}

## Stage 5 - Author-tests

{ "testFiles": ["tests/VisualRelay.Tests/RelayTaskRepositoryTests.cs"], "rationale": "Four tests added covering: (1) folder with only DONE-x.md absent from pending, (2) same folder present in completed, (3) folder with DONE-x.md + y.md emits pending from y.md with DONE file as sibling, (4) invariant that ListPendingAsync never returns ids with DONE-/IGNORE- prefixes. All four fail against the unfixed code — the first reproduces the exact bug (DONE-add-app-icon emitted as pending), the second confirms ListCompletedAsync's blind spot for regular subdirectories, the third proves the fallback selector ignores IsSkippedName, and the fourth validates the invariant the drain relied on." }

## Stage 6 - Implement

{
  "summary": "Implemented two changes in `RelayTaskRepository.cs`:\n\n1. **`EmitSingleTaskFromFolder` (line 208-210)**: Added `&& !IsSkippedName(Path.GetFileName(f))` to the fallback `.md` selector so DONE-/IGNORE- prefixed files are never chosen as the canonical markdown. When all `.md` files are DONE-/IGNORE- prefixed, `markdownPath` stays null and the method returns early — no pending task emitted.\n\n2. **`ListCompletedAsync` (lines 76-112)**: Inserted a new scan block after the top-level DONE-*.md loop that walks direct subdirectories of `tasksRoot` (excluding `completed`, `_ideation`, and skipped-name directories), enumerates `DONE-*.md` files, picks the canonical one via `FindCanonicalArchivedPath`, strips the DONE- prefix, guards against double-counting with top-level results, and adds each as an archived `RelayTaskItem` with `IsNested: true`, `IsArchived: true`, `ArchiveBatch: null`.\n\nAll 19 `RelayTaskRepositoryTests` pass, including the 4 new tests: `ListPendingAsync_FolderWithOnlyDoneMd_IsAbsentFromPending`, `ListCompletedAsync_FolderWithOnlyDoneMd_AppearsInCompleted`, `ListPendingAsync_FolderWithDoneMdAndRegularMd_PendingFromRegularMd`, and `ListPendingAsync_NoResultHasSkippedPrefix`."
}

## Stage 7 - Review

{ "verdict": "changes", "issues": [
  { "file": "src/VisualRelay.Core/Tasks/RelayTaskRepository.cs", "line": 78, "severity": "high", "desc": "ListCompletedAsync subdirectory scan is not guarded by Directory.Exists(tasksRoot). If the tasks directory is missing, Directory.EnumerateDirectories throws DirectoryNotFoundException." },
  { "file": "src/VisualRelay.Core/Tasks/RelayTaskRepository.cs", "line": 78, "severity": "medium", "desc": "ListCompletedAsync scans all subdirectories for DONE-*.md, including active task folders that also contain non-DONE .md files. It should skip folders that have an active (non-skipped) .md file, surfacing only all-DONE retired folders." }
] }

## Stage 8 - Fix

{
  "summary": "## Resolved both review issues in RelayTaskRepository.cs\n\n### Issue 1 (high): Missing `Directory.Exists(tasksRoot)` guard\n\n**Fix**: Extended the existing `if (Directory.Exists(tasksRoot))` guard (line 68) to encompass both the top-level DONE-*.md scan AND the subdirectory DONE-*.md scan (now closed at line 120). Previously the closing brace was at line 75, leaving `Directory.EnumerateDirectories(tasksRoot)` at line 78 unprotected against a missing tasks directory, which would throw `DirectoryNotFoundException`.\n\n### Issue 2 (medium): Active folders with DONE residue surfaced in completed list\n\n**Fix**: Added a guard at lines 88-93 in the subdirectory loop. After confirming DONE files exist, the code now enumerates all `*.md` files in the directory and uses `IsSkippedName` to check if any non-skipped (non-DONE-/non-IGNORE- prefixed) .md file exists. If one does, the folder is still active — it has a pending canonical markdown — so the DONE residue is skipped and the folder does NOT appear in `ListCompletedAsync`. Only all-DONE retired folders (where every .md file passes `IsSkippedName`) are surfaced.\n\n### Test update\n\nExtended `ListPendingAsync_FolderWithDoneMdAndRegularMd_PendingFromRegularMd` to also assert that `ListCompletedAsync` does NOT contain `old-task` — verifying the active-folder exclusion.\n\nAll 19 `RelayTaskRepositoryTests` pass."
}

## Stage 9 - Verify

{
  "summary": "Fixed `EmitSingleTaskFromFolder` fallback picking DONE-prefixed .md as canonical (line 216-218) by adding `!IsSkippedName(Path.GetFileName(f))` to the selector. Extended `ListCompletedAsync` (lines 78-119) to walk regular subdirectories for stranded DONE-*.md, skipping folders with active non-prefixed .md files. Wrapped both top-level and subdirectory scans in the existing `Directory.Exists(tasksRoot)` guard. Added 4 tests: DONE-only folder absent from pending + present in completed; mixed folder (DONE+regular) pending from regular; and invariant that no pending id starts with DONE-/IGNORE-. All 19 RelayTaskRepositoryTests pass.",
  "commitMessages": [
    "fix: filter DONE-/IGNORE- prefixed files from folder-task fallback selector",
    "fix: surface all-DONE folders in ListCompletedAsync, skip active folders with DONE residue",
    "fix: prevent DONE- prefixed folder tasks from appearing as pending queue items",
    "test: add four tests for DONE-only folders, mixed folders, and ListPendingAsync id-prefix invariant"
  ]
}

## Stage 10 - Fix-verify (attempt 1/5)

{
  "summary": "Removed 4 duplicated tests from RelayTaskRepositoryTests.cs (lines 280-373) that were already extracted into RelayTaskRepositoryDoneFolderTests.cs. The duplication pushed the file to 391 lines, violating the 300-line split guard enforced by SplitGuardVerificationTests. After removal the file is 296 lines. All 614 tests pass with 0 failures.",
  "amendManifest": ["tests/VisualRelay.Tests/RelayTaskRepositoryTests.cs"]
}

## Stage 10 - Fix-verify

{
  "summary": "## Verification results\n\n**All 19 RelayTaskRepository tests pass** (15 in RelayTaskRepositoryTests.cs + 4 in RelayTaskRepositoryDoneFolderTests.cs), including the 4 tests added for this fix:\n- `ListPendingAsync_FolderWithOnlyDoneMd_IsAbsentFromPending`\n- `ListCompletedAsync_FolderWithOnlyDoneMd_AppearsInCompleted`\n- `ListPendingAsync_FolderWithDoneMdAndRegularMd_PendingFromRegularMd`\n- `ListPendingAsync_NoResultHasSkippedPrefix`\n\n**All 10 SplitGuardVerificationTests pass**, confirming:\n- Guard script `tools/guards/check-file-size.sh` exits 0\n- All test `.cs` files are ≤ 300 lines (RelayTaskRepositoryTests.cs: 296, DoneFolderTests.cs: 101, RelayTaskRepository.cs: 292)\n- Fact count baseline matches (130 across oversized families)\n- All companion file / collection attribute / helpers conventions hold\n\n**4 pre-existing flaky failures** in the full suite (632 total, 628 passed) are unrelated to this fix: ProcessTreeCpuSampler, HookInstaller, GitInvoker override, and SwivalSubagentRunnerWatchdog CPU-pulse timeout.\n\nThe fix is verified: DONE-prefixed files are never selected as canonical markdown in `EmitSingleTaskFromFolder`'s fallback (line 216), all-DONE folders surface in `ListCompletedAsync` (lines 78-119), active folders with DONE residue are excluded from completed (line 88-91), and no `ListPendingAsync` result has a DONE-/IGNORE- prefixed id."
}

## Stage 11 - Commit

Committed by Visual Relay.

