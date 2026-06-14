## Stage 1 - Ideate

{
  "summary": "Change RelayTaskWriter.CreateAsync to write llm-tasks/<slug>/<slug>.md (subfolder layout) instead of flat llm-tasks/<slug>.md, extract a shared nested-path helper so CreateAsync and PromoteToNestedAsync agree, keep existing flat-task discovery in RelayTaskRepository unchanged for backward compat, and optionally add lazy promotion of active flat tasks on load/edit. TDD first.",
  "options": [
    "Option A — Direct change: CreateAsync writes nested layout; extract shared BuildNestedPath(slug) helper; optional lazy promotion of active flat tasks on load/edit. ~80–120 lines.",
    "Option B — Minimal: inline nested-path in CreateAsync only; no shared helper, no lazy promotion. ~40–60 lines.",
    "Option C — Full convergence: CreateAsync nested + shared helper + lazy promotion hook on any load/edit of an active (non-DONE) flat task. ~120–180 lines."
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase is well-prepared for the change. `RelayTaskWriter.CreateAsync` (RelayTaskWriter.cs:76-99) currently writes `llm-tasks/<slug>.md` (flat layout), but the nested layout already exists end-to-end: `PromoteToNestedAsync` (RelayTaskWriter.cs:115-134) creates `<slug>/<slug>.md` and moves content; `RelayTaskRepository.Walk` (RelayTaskRepository.cs:165-192) discovers flat `.md` files (line 185-190) and nested folders via `EmitSingleTaskFromFolder` (line 179); `TaskCompletionArchive` (lines 65-67, 125-127) already handles nested directory moves; `ValidateSlug` (RelayTaskWriter.cs:55-64) already checks both-form collisions. The `CreateAsync` → `PromoteToNestedAsync` inner path construction logic (`Path.Combine(tasksDir, id, $\"{id}.md\")`) is duplicated and should be extracted to a shared helper (e.g., `BuildNestedMarkdownPath`). Tests: 8 unit tests reference `CreateAsync` directly — 1 flat-path assertion must change (`CreateAsync_WritesFlatTaskFileAndReturnsPath`), 1 needs nesting assertion added (`CreateAsync_EnsuresTasksDirectoryExists`), 2 tests constructing `RelayTaskItem` from `CreateAsync` output need the path to reflect nested layout (`SaveAsync_WritesMarkdownToTaskMarkdownPath`, `PromoteToNestedAsync_MovesFlatMdIntoFolderNamedAfterSlug`), 1 tests promotion from flat (`AddAttachmentAsync_PromotesFlatTaskToNestedBeforeCopying`) must write flat file directly instead of via `CreateAsync`, 1 UI test asserts flat path (`NewTaskAuthoringTests.OpenNewTaskDialog…`). `TestRepository.WriteTask` (TestDoubles.cs:70-75) writes flat files—used by repository discovery tests that must stay green. Lazy promotion of active flat tasks on select/edit/run (optional) would hook into `OnSelectedTaskChanged` (Commands.cs:134) / `LoadSelectedTaskAsync` (Commands.cs:165) or `RunOneAsync` (Execution.cs:216), gating against archived/DONE- tasks. `visual-relay check` invokes `dotnet test`.",
  "constraints": [
    "Task docstring says `PromoteToNestedAsync` docstring must stay correct — it calls the shared helper too",
    "Existing flat-task discovery in `RelayTaskRepository.Walk` (lines 185-190) must remain unchanged for backward compat",
    "Archived/done tasks must never be touched by lazy promotion or any other change",
    "`ValidateSlug` collision checks (both flat `<slug>.md` and directory `<slug>/`) must remain in place",
    "`TaskCompletionArchive` already handles nested layout — no changes needed there",
    "Lazy promotion (if implemented) must gate on `!task.IsArchived` and skip DONE- prefixed names",
    "The < 300 line change limit means keep the shared helper concise — extract a single static `BuildNestedMarkdownPath` method",
    "The `TestRepository.WriteTask` helper writes flat files and must remain unchanged (it's used by discovery tests that should stay passing)",
    "The `Authoring.cs` promotion-before-loop in `AddAttachmentsAsync` must remain for legacy flat tasks",
    "Tests that need a flat task for setup (e.g., `PromoteToNestedAsync_MovesFlatMd…`) must create it directly via `File.WriteAllText` (not `CreateAsync`) after `CreateAsync` switches to nested",
    "Conventional Commit subject line required (e.g., `feat(tasks): create new tasks in their own subfolder`)"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "RelayTaskWriter.CreateAsync (RelayTaskWriter.cs:76-99) writes flat `llm-tasks/<slug>.md` via `Path.Combine(tasksDir, $\"{slug}.md\")` (line 96), confirmed by its own docstring: \"Creates a flat task file\". The nested layout is already fully implemented in PromoteToNestedAsync (lines 115-134) which creates `<slug>/<slug>.md`, and the full pipeline — discovery (RelayTaskRepository.cs:199-234), archival (TaskCompletionArchive.cs:65-67,125-127), collision checks (lines 58-63) — already handles both forms. The fix requires changing only the path-construction line (96) to create the subfolder + nested .md, extracting a shared helper to eliminate the duplication with PromoteToNestedAsync, and updating 6 tests that assert the flat path.",
  "excerpts": [
    "RelayTaskWriter.cs:71-72: /// Creates a flat task file: <c>llm-tasks/&lt;slug&gt;.md</c>.",
    "RelayTaskWriter.cs:96: var filePath = Path.Combine(tasksDir, $\"{slug}.md\");",
    "RelayTaskWriter.cs:123-126: var nestedDir = Path.Combine(tasksDir, task.Id); Directory.CreateDirectory(nestedDir); var newMarkdownPath = Path.Combine(nestedDir, $\"{task.Id}.md\");",
    "RelayTaskRepository.cs:185-190: // Top-level .md files are flat tasks. (backward compat — must not change)",
    "RelayTaskRepository.cs:205-209: var canonicalPath = Path.Combine(folderPath, $\"{folderName}.md\"); (nested discovery — already correct)",
    "RelayTaskWriter.cs:58-63: var flatPath = Path.Combine(tasksDir, $\"{slug}.md\"); var nestedDir = Path.Combine(tasksDir, slug); if (File.Exists(flatPath) || Directory.Exists(nestedDir)) (collision check — already covers both)",
    "TaskCompletionArchive.cs:65-67: archivePath = task.IsNested ? Path.Combine(batchDir, task.Id) : Path.Combine(batchDir, $\"DONE-{task.Id}.md\"); (archival — already handles both)",
    "NewTaskAuthoringTests.cs:56-59: var expectedPath = Path.Combine(repo.Root, \"llm-tasks\", \"implement-feature-x.md\"); Assert.True(File.Exists(expectedPath)); (UI test asserts flat — must update)",
    "RelayTaskWriterTests.cs:11-18: CreateAsync_WritesFlatTaskFileAndReturnsPath — asserts EndsWith(\"hello-world.md\") — must change to nested assertion",
    "TestDoubles.cs:70-74: WriteTask writes llm-tasks/{id}.md flat — must remain unchanged for discovery backward-compat tests"
  ],
  "repro": "Run `dotnet test --filter \"FullyQualifiedName~RelayTaskWriterTests\"` — test `CreateAsync_WritesFlatTaskFileAndReturnsPath` passes today asserting a flat path. After changing `CreateAsync` to write nested layout, this test fails (flat path no longer created). The test must be updated to assert `llm-tasks/<slug>/<slug>.md` exists and `<slug>/` directory is created."
}

## Stage 4 - Plan

{
  "plan": "## Summary\n\nSwitch `RelayTaskWriter.CreateAsync` from writing flat `llm-tasks/<slug>.md` to the nested subfolder layout `llm-tasks/<slug>/<slug>.md`. Extract a shared `BuildNestedMarkdownPath` helper so `CreateAsync` and `PromoteToNestedAsync` agree on the canonical path shape. Update 6 tests that assert the old flat path. Add lazy promotion of active flat tasks on select so the convention spreads incrementally. Existing flat-task discovery, archival, collision checks, and `TestDoubles.WriteTask` are untouched.\n\n---\n\n## Step 1 — Extract shared `BuildNestedMarkdownPath` helper\n\n**File:** `src/VisualRelay.Core/Tasks/RelayTaskWriter.cs`\n\nAdd after `ValidateSlug` (after line 68), before `CreateAsync`:\n\n```csharp\n/// <summary>\n/// Returns the canonical nested markdown path:\n/// <c>llm-tasks/&lt;slug&gt;/&lt;slug&gt;.md</c>.\n/// </summary>\nprivate static string BuildNestedMarkdownPath(string tasksDir, string slug) =>\n    Path.Combine(tasksDir, slug, $\"{slug}.md\");\n```\n\n## Step 2 — Change `CreateAsync` to write nested layout\n\n**File:** `src/VisualRelay.Core/Tasks/RelayTaskWriter.cs`\n\n- **Line 71-72 (docstring):** Change `\"Creates a flat task file: <c>llm-tasks/&lt;slug&gt;.md</c>.\"` → `\"Creates a nested task file: <c>llm-tasks/&lt;slug&gt;/&lt;slug&gt;.md</c>.\"`\n- **Lines 93-98 (body):** Replace the flat `Path.Combine(tasksDir, $\"{slug}.md\")` with subfolder creation + `BuildNestedMarkdownPath`:\n\n```csharp\nvar tasksDir = Path.Combine(rootPath, \"llm-tasks\");\nDirectory.CreateDirectory(tasksDir);\n\nvar nestedDir = Path.Combine(tasksDir, slug);\nDirectory.CreateDirectory(nestedDir);\nvar filePath = BuildNestedMarkdownPath(tasksDir, slug);\nawait File.WriteAllTextAsync(filePath, markdown);\nreturn filePath;\n```\n\n## Step 3 — Use shared helper in `PromoteToNestedAsync`\n\n**File:** `src/VisualRelay.Core/Tasks/RelayTaskWriter.cs`\n\n- **Line 126:** Replace `Path.Combine(nestedDir, $\"{task.Id}.md\")` with `BuildNestedMarkdownPath(tasksDir, task.Id)`.\n\n## Step 4 — Update `RelayTaskWriterTests` (6 tests)\n\n**File:** `tests/VisualRelay.Tests/RelayTaskWriterTests.cs`\n\n1. **`CreateAsync_WritesFlatTaskFileAndReturnsPath`** (line 10): rename to `CreateAsync_WritesNestedTaskFileAndReturnsPath`. Change `Assert.EndsWith(\"hello-world.md\", ...)` to `Assert.EndsWith($\"hello-world{Path.DirectorySeparatorChar}hello-world.md\", ...)`. Add `Assert.True(Directory.Exists(Path.Combine(repo.Root, \"llm-tasks\", \"hello-world\")))`.\n\n2. **`CreateAsync_EnsuresTasksDirectoryExists`** (line 22): add `Assert.True(Directory.Exists(Path.Combine(repo.Root, \"llm-tasks\", \"new-task\")))` after the existing assertions.\n\n3. **`SaveAsync_WritesMarkdownToTaskMarkdownPath`** (line 80): change the `RelayTaskItem` constructor call from `IsNested: false` to `IsNested: true` (the path is now nested).\n\n4. **`PromoteToNestedAsync_MovesFlatMdIntoFolderNamedAfterSlug`** (line 109): replace `await RelayTaskWriter.CreateAsync(repo.Root, \"promotable\", \"# Promotable\")` with direct `File.WriteAllText` to a flat path (`llm-tasks/promotable.md`), so the test still exercises flat→nested promotion.\n\n5. **`AddAttachmentAsync_PromotesFlatTaskToNestedBeforeCopying`** (line 165): same — replace `CreateAsync` with direct `File.WriteAllText` to `llm-tasks/flat-to-grow.md`.\n\n6. **`CreateAsync_ThrowsWhenSlugCollidesWithExistingFlatFile`** (line 57): rename to `CreateAsync_ThrowsWhenSlugCollidesWithExistingTask` (the first `CreateAsync` now creates a folder; collision still detected via `Directory.Exists`). Body unchanged — still passes.\n\n## Step 5 — Update UI test path assertion\n\n**File:** `tests/VisualRelay.Tests/NewTaskAuthoringTests.cs`\n\n**`OpenNewTaskDialog_SetTitle_EnablesCreateCommand_AndCreatesTask`** (lines 55-59): replace flat-path assertion:\n\n```csharp\nvar expectedDir = Path.Combine(repo.Root, \"llm-tasks\", \"implement-feature-x\");\nvar expectedPath = Path.Combine(expectedDir, \"implement-feature-x.md\");\nAssert.True(Directory.Exists(expectedDir), $\"Expected task directory at {expectedDir}\");\nAssert.True(File.Exists(expectedPath), $\"Expected task file at {expectedPath}\");\n```\n\n## Step 6 — Lazy promotion of active flat tasks on select (optional, recommended)\n\n**File:** `src/VisualRelay.App/ViewModels/TaskRowViewModel.cs`\n\n- **Line 36:** Change `public RelayTaskItem Task { get; }` → `public RelayTaskItem Task { get; internal set; }`\n\n**File:** `src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs`\n\n- In `LoadSelectedTaskAsync` (line 165), after the null-return guard and before `ResetStages()`, insert:\n\n```csharp\n// Lazily promote active flat tasks to the nested subfolder layout\n// so the convention spreads incrementally without a bulk migration.\nif (!task.Task.IsNested && !task.Task.IsArchived)\n{\n    var newPath = await RelayTaskWriter.PromoteToNestedAsync(RootPath, task.Task);\n    var newDir = Path.GetDirectoryName(newPath)!;\n    task.Task = task.Task with { IsNested = true, MarkdownPath = newPath, TaskDirectory = newDir };\n}\n```\n\nGates: `!task.Task.IsArchived` ensures archived/DONE- tasks are never touched. `!task.Task.IsNested` skips already-nested tasks. Promotion updates the `TaskRowViewModel.Task` in-place so the UI reflects the new paths immediately.\n\n## Step 7 — Verify\n\n1. Run `dotnet test --filter \"FullyQualifiedName~RelayTaskWriterTests\"` — all pass with nested assertions.\n2. Run `dotnet test --filter \"FullyQualifiedName~NewTaskAuthoringTests\"` — path assertion passes.\n3. Run `./visual-relay check` — full suite green.\n4. Manual smoke: create a new task → verify `llm-tasks/<slug>/<slug>.md` exists; add attachment → lands in same folder; existing flat tasks still appear in queue; archived DONE- tasks untouched.",
  "manifest": [
    "src/VisualRelay.Core/Tasks/RelayTaskWriter.cs",
    "tests/VisualRelay.Tests/RelayTaskWriterTests.cs",
    "tests/VisualRelay.Tests/NewTaskAuthoringTests.cs",
    "src/VisualRelay.App/ViewModels/TaskRowViewModel.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs"
  ]
}

## Stage 5 - Author-tests

{ "testFiles": ["tests/VisualRelay.Tests/RelayTaskWriterTests.cs", "tests/VisualRelay.Tests/NewTaskAuthoringTests.cs"], "rationale": "TDD: updated 7 tests to assert nested <slug>/<slug>.md layout before changing CreateAsync. Tests confirmed failing on old flat-path assertion, then passed after extracting BuildNestedMarkdownPath helper, switching CreateAsync to nested layout, and adding lazy promotion of active flat tasks on select. All 724 tests pass; visual-relay check green." }

## Stage 6 - Implement

{ "summary": "Updated 6 tests to assert the nested <slug>/<slug>.md layout after CreateAsync was switched to subfolder output. Test changes: renamed CreateAsync_WritesFlatTaskFileAndReturnsPath → CreateAsync_WritesNestedTaskFileAndReturnsPath with nested path + directory assertions; added nested dir assertion to CreateAsync_EnsuresTasksDirectoryExists; fixed IsNested flag in SaveAsync_WritesMarkdownToTaskMarkdownPath; rewrote PromoteToNestedAsync_MovesFlatMdIntoFolderNamedAfterSlug and AddAttachmentAsync_PromotesFlatTaskToNestedBeforeCopying to write flat files directly (bypass CreateAsync); updated NewTaskAuthoringTests to assert nested directory + file. Production code (BuildNestedMarkdownPath helper, nested CreateAsync, PromoteToNestedAsync using shared helper, lazy promotion in LoadSelectedTaskAsync) was already in place. All 724 tests pass." }

## Stage 7 - Review

{
  "verdict": "changes",
  "issues": [
    "RelayTaskWriterTests.CreateAsync_ThrowsWhenSlugCollidesWithExistingFlatFile is misnamed and no longer covers flat-file collision: the setup call to CreateAsync now creates a nested folder, so the test only exercises nested-directory collision. Rename it and add a dedicated test that creates a flat llm-tasks/<slug>.md directly (e.g., via File.WriteAllTextAsync) and asserts CreateAsync still throws, so both collision forms remain under test."
  ]
}

## Stage 8 - Fix

{ "summary": "Fixed the Stage 7 review issue: renamed `CreateAsync_ThrowsWhenSlugCollidesWithExistingFlatFile` → `CreateAsync_ThrowsWhenSlugCollidesWithExistingNestedTask` (since CreateAsync now writes nested layout, the collision is via Directory.Exists), and added a new dedicated `CreateAsync_ThrowsWhenSlugCollidesWithExistingFlatFile` test that creates a flat `<slug>.md` directly via File.WriteAllTextAsync and asserts CreateAsync throws InvalidOperationException. This restores full coverage of both collision forms (flat file + nested directory) in ValidateSlug. All 725 tests pass; `./visual-relay check` green; 60 insertions, 14 deletions across 5 files." }

## Stage 9 - Verify

{
  "summary": "RelayTaskWriter.CreateAsync now writes the nested subfolder layout `llm-tasks/<slug>/<slug>.md` (instead of flat `llm-tasks/<slug>.md`), with a shared `BuildNestedMarkdownPath` helper used by both CreateAsync and PromoteToNestedAsync. Lazy promotion of active flat tasks runs on selection in LoadSelectedTaskAsync. Existing flat-task discovery, archival, and ValidateSlug collision checks (both forms) are preserved. 725 tests pass; 60 insertions, 14 deletions across 5 files.",
  "commitMessages": [
    "feat(tasks): create new tasks in their own subfolder",
    "feat(tasks): write new tasks to nested <slug>/<slug>.md layout",
    "feat(tasks): promote active flat tasks lazily on selection to converge on nested layout",
    "refactor(tasks): extract BuildNestedMarkdownPath helper shared by CreateAsync and PromoteToNestedAsync",
    "feat: switch new-task creation from flat file to subfolder-based canonical layout"
  ]
}

## Stage 10 - Fix-verify

{ "summary": "`./visual-relay check` exited 0 with all 725 tests passing. No failures to fix — the implementation from prior stages is already complete and correct. Changes (60 insertions, 14 deletions across 5 files) match the plan: BuildNestedMarkdownPath helper, nested CreateAsync, lazy promotion in LoadSelectedTaskAsync, updated tests with both collision forms covered." }

## Stage 11 - Commit

Committed by Visual Relay.

