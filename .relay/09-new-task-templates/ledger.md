## Stage 1 - Ideate

{
  "summary": "Implement a three-layer (built-in/user/repo) new-task template system with a dropdown in the New-task dialog. The spec prescribes exact file contents, line changes, and test assertions across ~12 files. Three viable approaches differ in how built-in templates are shipped (embedded resources vs C# constants) and whether template loading is static or DI-backed.",
  "options": [
    "A — Exact spec compliance: embedded resources in VisualRelay.Core.csproj + static TaskTemplates class, matching the NonoProfileEnsurer pattern. Lowest risk, best alignment with existing conventions, but requires csproj edit and resource-stream plumbing.",
    "B — C# string constants for built-ins: define template content as const fields in TaskTemplates.cs, avoid embedded resources entirely. Eliminates csproj touch and resource-stream fragility, but breaks from the project's established packaging convention and increases C# file size with prose content.",
    "C — Interface-based ITaskTemplateProvider with DI: three separate providers composed by a composite. Best long-term extensibility and clean test mocking, but is over-engineered for the current scope — the rest of the codebase uses statics for this kind of infra, and adding DI plumbing for a single dropdown is disproportionate."
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase is well-structured with clear patterns to replicate: (1) Embedded resources follow the NonoProfileEnsurer pattern — csproj LogicalName + GetManifestResourceStream + StreamingReader. packaging/task-templates/ must be created alongside packaging/nono/. (2) BuildNonoPrefix in ProcessRunners.cs (lines 151-183) is the static nono-arg builder; adding a 5th optional parameter userTemplatesDirOverride and a Directory.CreateDirectory + -a pair after SandboxExtraAllowPaths is the required change. (3) RelayTaskRepository.cs SkippedDirectories (line 9) needs 'templates' added. (4) RelayTaskWriter.cs ValidateSlug (lines 40-46) needs the 'completed'/'templates' reserved-name check. (5) OpenNewTaskDialog in MainWindowViewModel.Authoring.cs gets PrepareNewTaskTemplates() on line 249. (6) TaskDetailPanel.axaml new-task grid (lines 172-205) shifts from RowDefinitions='Auto,*,Auto' to 'Auto,Auto,*,Auto' with template row inserted at row 0. (7) TestRepository redirects XDG_CONFIG_HOME to repo.Root, so user template tests resolve to correct isolated paths. (8) 6 existing test files assert exact BuildNonoPrefix argv indices and need updating for the new -a <templatesDir> pair. (9) README.md has space between '# What Visual Relay Does' (ends line 65) and '# Tests' (line 67). (10) All file budgets have headroom — MainWindowViewModel.cs is at exactly 300 (no touch), Authoring.cs at 294 (1 new line = 295 max), RelayTaskWriter.cs at 296 (299 max), ProcessRunners.cs at 184 (200 max), TaskDetailPanel.axaml at 275 (292 max).",
  "constraints": [
    "Touch only the ~12 files listed in spec — no modifications to MainWindowViewModel.cs, RelayTaskWriter.CreateAsync, RelayDriver.CodeChangeGate.cs, WorktreeFilter.cs, WorktreeResetter.cs, GitCommitter.Untracked.cs, or packaging/nono/vr-guard.json.",
    "MainWindowViewModel.cs must remain at exactly 300 lines (currently 300) — zero changes.",
    "MainWindowViewModel.Authoring.cs max 295 lines (currently 294) — exactly one line added.",
    "RelayTaskWriter.cs max 299 lines (currently 296) — adding ~3 lines for the reserved name check.",
    "RelayTaskRepository.cs max 291 lines — edit one line in place (SkippedDirectories array).",
    "ProcessRunners.cs max 200 lines (currently 184).",
    "TaskDetailPanel.axaml max 292 lines (currently 275).",
    "TaskTemplates.cs new file ≤ 300 lines.",
    "MainWindowViewModel.Templates.cs new file ≤ 300 lines.",
    "No bare empty catch blocks — each 'catch (Exception)' must have the required comment.",
    "No language/framework/runner-specific content in speed-up template — pinned by DoesNotContain('dotnet') test.",
    "No YAML library — hand-parse frontmatter like the rest of the repo's line-based config parsing.",
    "No DI or interface-based template provider — use static TaskTemplates class like NonoProfileEnsurer.",
    "No config key / RelayConfig.TasksDir plumbing for template locations — fixed well-known paths only.",
    "No template management UI, file watchers, or caching — files are the interface, re-enumerate on every dialog open.",
    "No repo templates under .relay/ — they go under llm-tasks/templates/.",
    "No SelectedItem binding on dropdown names — use index binding to avoid cross-layer name collisions.",
    "Do not create llm-tasks/templates/ content in the repo as part of this task.",
    "userTemplatesDirOverride parameter on BuildNonoPrefix is for tests only — production callers pass nothing.",
    "All 6 existing arg-assertion test files must be updated with the new -a <templatesDir> pair — tests must not be deleted or weakened.",
    "Conventional Commits; minimal diffs; zero new InspectCode findings.",
    "The speed-up template body is verbatim — keep every line including the pinned commit-bullet line."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "All file budgets confirmed via wc -l: MainWindowViewModel.cs=300 (at cap, do not touch), MainWindowViewModel.Authoring.cs=294 (cap 295, +1 line), RelayTaskWriter.cs=296 (cap 299, +3 lines), RelayTaskRepository.cs=291 (edit-in-place, stays 291), ProcessRunners.cs=184 (cap 200, ~+10 lines), TaskDetailPanel.axaml=275 (cap 292, ~+8 lines), README.md=100 (new section between lines 65-67). No TaskTemplates.cs, no MainWindowViewModel.Templates.cs, no packaging/task-templates/ exist yet. csproj has exactly one EmbeddedResource for vr-guard.json in its own <ItemGroup>. BuildNonoPrefix signature confirmed: `(RelayConfig config, bool rollback, IReadOnlyList<string>? skipDirs = null, bool verboseDiagnostics = false)` — adding 5th optional `userTemplatesDirOverride` is backward-compatible. Two production callers: ProcessRunners.Helpers.cs:27 and SandboxedTestRunner.cs:56, both pass no override. NonoProfileEnsurer.ReadEmbedded pattern confirmed (GetManifestResourceStream + null check → InvalidOperationException + StreamReader). TestRepository sets XDG_CONFIG_HOME=repo.Root, so user templates resolve to <repo.Root>/visual-relay/templates. NewTaskAuthoringTests has [Collection(\"Headless\")]; new partial must not duplicate the attribute. CreateNewTaskAsync lines 268-270 prepend '# {title}\\n\\n' to body — confirmed, matching spec's double-heading rule. RelayTaskWriter.CreateAsync line 100 hardcodes 'llm-tasks'. RelayTaskRepository SkippedDirectories line 9 currently ['completed', '_ideation']. IsBookkeepingPath ends with IsPathUnderDirectory(rootPath, path, tasksDir) — templates subdir is covered. IsUnderTasksDir uses prefix checks on llm-tasks/ — templates subdir already covered. 6 test files assert exact BuildNonoPrefix argv indices and must be updated: SwivalSubagentRunnerSandboxTests.cs (270 lines, exact array assertions at lines 41, 125, 138, 153-169, 184-190, 204-213), SwivalSubagentRunnerSandboxTests.SkipDirs.cs (88 lines, exact arrays at lines 51, 64), SandboxExtraAllowPathsConfigTests.cs (262 lines, index assertions at lines 230-232, 247-250), SandboxDiagnosticsToggleTests.cs (131 lines, Take(4) at line 58 — still matches, Contains-based checks survive; the Silent_IsOutputOnly_SandboxUnchanged at line 63 uses Where filter so robust), SandboxedTestRunnerArgumentTests.cs (268 lines, exact arrays at lines 25, 48, 64, 73, 83-91, 116-119), NonoLaunchDriftGuardTests.cs (77 lines, Take(4) at line 26 survives; filter-based equality at line 36-37 survives since both agent+verify get same templates grant).",
  "excerpts": [
    "TaskDetailPanel.axaml actual path: src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml (spec says src/VisualRelay.App/Views/TaskDetailPanel.axaml — missing Controls/ subdirectory).",
    "RelayTaskWriter.ValidateSlugForRename (line 252-281) has its own copy of the reserved-prefix check (lines 259-262: `if (slug.StartsWith(\"DONE-\"...) || slug.StartsWith(\"IGNORE-\"...))`) that won't be updated per spec's 299-line budget and 'Do not add anything else to this file' constraint. Renaming a task to 'templates' or 'completed' would succeed while creating one would be blocked — intentional omission given budget.",
    "SwivalSubagentRunnerSandboxTests.cs line 169: `Assert.Equal(swivalPrefix.Count - 2, verifyPrefix.Count)` — after adding the templates grant (same in both prefixes), the count relationship still holds but exact indices referenced nearby (e.g. line 159 `Assert.Equal(\"--rollback\", swivalPrefix[4])`) will shift by +2 after the new -a <templatesDir> pair.",
    "SandboxExtraAllowPathsConfigTests.cs lines 230-232: `Assert.Equal(\"-a\", prefix[4]); Assert.Equal(extra, prefix[5]); Assert.Equal(\"--rollback\", prefix[6])` — after the templates -a pair, these become prefix[6]/[7]/[8]. Similarly lines 247-250 for verification prefix shift from [4]/[5]/[6]/[7] to [6]/[7]/[8]/[9].",
    "SandboxedTestRunnerArgumentTests.cs line 25: `args.Take(6)` asserts `{ \"run\", \"--profile\", ProfilePath, \"--allow-cwd\", \"--silent\", \"--\" }` — the templates -a pair will appear between --allow-cwd (index 3) and --silent, so Take(6) becomes Take(8). Lines 48, 64, 73, 83-91, 116-119 all have similar index-pinned assertions.",
    "BuildNonoPrefix_TwoCallerPreamblesDifferOnlyInRollbackFlags (SwivalSubagentRunnerSandboxTests.cs line 142) asserts swivalPrefix.Take(4) = {run, --profile, <abs>, --allow-cwd} — this survives since templates grant is after index 3. But lines 159-169 assert exact indices [4] through [7] which shift.",
    "NonoLaunchDriftGuardTests.cs line 36: `agentCore = agentPrefix.Where(x => x is not \"--rollback\" and not \"--no-rollback-prompt\").ToList(); Assert.Equal(agentCore, verifyPrefix)` — survives because both prefixes get the identical templates -a pair. Line 26 `Assert.Equal(head, agentPrefix.Take(4))` also survives."
  ],
  "repro": "1. Confirm budgets: `wc -l src/VisualRelay.App/ViewModels/MainWindowViewModel.cs src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs src/VisualRelay.Core/Tasks/RelayTaskWriter.cs src/VisualRelay.Core/Tasks/RelayTaskRepository.cs src/VisualRelay.Core/Execution/ProcessRunners.cs src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml README.md` → 300, 294, 296, 291, 184, 275, 100. 2. Check packaging/task-templates/ doesn't exist: `ls packaging/task-templates/` → no match. 3. Verify BuildNonoPrefix signature: `grep -n 'internal static IReadOnlyList<string> BuildNonoPrefix' src/VisualRelay.Core/Execution/ProcessRunners.cs` → line 151 with 4 params. 4. Confirm ValidateSlugForRename has separate prefix check: `grep -n 'DONE-\\|IGNORE-' src/VisualRelay.Core/Tasks/RelayTaskWriter.cs` → lines 42-43 (ValidateSlug) and lines 259-260 (ValidateSlugForRename). 5. Verify production BuildNonoPrefix callers pass no 5th arg: `grep -n 'BuildNonoPrefix' src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs src/VisualRelay.Core/Execution/SandboxedTestRunner.cs` → lines 27 and 56, both 4-arg calls. 6. Confirm 6 test files that assert exact argv indices: `grep -ln 'BuildNonoPrefix.*TestConfig\\|prefix\\[[0-9]\\]\\|\\.Take([0-9])\\|new\\[\\] { \"run\"' tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.SkipDirs.cs tests/VisualRelay.Tests/SandboxExtraAllowPathsConfigTests.cs tests/VisualRelay.Tests/SandboxDiagnosticsToggleTests.cs tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs tests/VisualRelay.Tests/NonoLaunchDriftGuardTests.cs`."
}

## Stage 4 - Plan

{
  "plan": "## Summary\n\nImplement a three-layer (built-in/user/repo) new-task template system. A Template dropdown in the New-task dialog prefills title and body from `.md` template files. Two built-in templates ship as embedded resources. Users add templates by dropping `.md` files into `~/.config/visual-relay/templates/` or `llm-tasks/templates/`. Template files use optional YAML-style frontmatter (`name:` for dropdown label, `title:` for task-title prefill). The dropdown re-enumerates on every dialog open.\n\n## Stage 1: New source files (infrastructure)\n\n### 1a. Create `packaging/task-templates/blank.md`\nExact content per spec §1 — empty body with frontmatter `name: Blank`.\n\n### 1b. Create `packaging/task-templates/speed-up-automated-tests.md`\nExact verbatim content per spec §1 — must include the pinned commit-bullet line and stack-agnostic coverage rules. \n\n### 1c. Create `src/VisualRelay.Core/Tasks/TaskTemplates.cs`\nNew static class in `VisualRelay.Core.Tasks` namespace (sits beside `RelayTaskWriter.cs`/`RelayTaskRepository.cs`):\n- `TaskTemplateSource` enum: `BuiltIn`, `User`, `Repo`\n- `TaskTemplate` record: `Id`, `Name`, `Title`, `Body`, `Source`\n- `ResolveUserTemplatesDir(IEnvironmentAccessor?)` → `Path.Combine(XdgConfig.ResolveConfigDir(accessor), \"visual-relay\", \"templates\")`\n- `Load(string userTemplatesDir, string repoTemplatesDir)` → IReadOnlyList<TaskTemplate>\n  - Seed `Dictionary<string, TaskTemplate>(OrdinalIgnoreCase)` with built-ins (read via `GetManifestResourceStream` like `NonoProfileEnsurer.ReadEmbedded`; throw `InvalidOperationException` on null stream)\n  - Overlay user dir, then repo dir (each: enumerate `*.md` top-level, wrap in try/catch with required comment, parse with `Parse`)\n  - Order: `blank` first, then by `Name` OrdinalIgnoreCase, then by `Id`\n- `internal static Parse(string id, string content, TaskTemplateSource source)` — normalize `\\r\\n`→`\\n`, split on `\\n`, parse frontmatter if `lines[0] == \"---\"`, extract `name:` and `title:` keys, body = rest with `TrimStart('\\n')`. Fallbacks: `Name`=id, `Title`=string.Empty.\n\n### 1d. Create `src/VisualRelay.App/ViewModels/MainWindowViewModel.Templates.cs`\nNew partial of `MainWindowViewModel` with:\n- `NewTaskTemplateNames` ObservableCollection<string>\n- `SelectedNewTaskTemplateIndex` [ObservableProperty]\n- `_newTaskTemplates` IReadOnlyList<TaskTemplate>\n- `_lastAppliedTemplateTitle` / `_lastAppliedTemplateBody`\n- `PrepareNewTaskTemplates()` — calls `TaskTemplates.Load()`, populates `NewTaskTemplateNames`, resets last-applied trackers, sets `SelectedNewTaskTemplateIndex` to `IndexOfBlank()` (with -1→index reset trick)\n- `IndexOfBlank()` — scans for id==\"blank\" case-insensitively, falls back to 0\n- `OnSelectedNewTaskTemplateIndexChanged(int)` — applies template title/body only when field is empty or matches last-applied value (never clobbers user edits)\n\n## Stage 2: Edit existing files (no new files)\n\n### 2a. `src/VisualRelay.Core/VisualRelay.Core.csproj`\nAdd two `<EmbeddedResource>` entries in the existing `<ItemGroup>` at line 12:\n```xml\n<EmbeddedResource Include=\"..\\..\\packaging\\task-templates\\blank.md\" LogicalName=\"VisualRelay.Core.task-templates.blank.md\" />\n<EmbeddedResource Include=\"..\\..\\packaging\\task-templates\\speed-up-automated-tests.md\" LogicalName=\"VisualRelay.Core.task-templates.speed-up-automated-tests.md\" />\n```\n\n### 2b. `src/VisualRelay.Core/Execution/ProcessRunners.cs`\n- Add `using VisualRelay.Core.Tasks;` to usings\n- Extend `BuildNonoPrefix` signature with `string? userTemplatesDirOverride = null` as 5th optional parameter\n- After the `SandboxExtraAllowPaths` block (line 164), insert the grant:\n  ```csharp\n  var templatesDir = userTemplatesDirOverride ?? TaskTemplates.ResolveUserTemplatesDir();\n  Directory.CreateDirectory(templatesDir);\n  args.Add(\"-a\");\n  args.Add(templatesDir);\n  ```\n\n### 2c. `src/VisualRelay.Core/Tasks/RelayTaskRepository.cs`\nLine 9: Change `SkippedDirectories` from `[\"completed\", \"_ideation\"]` to `[\"completed\", \"_ideation\", \"templates\"]`.\n\n### 2d. `src/VisualRelay.Core/Tasks/RelayTaskWriter.cs`\nReplace the reserved-prefix block (lines 40-46):\n```csharp\n        if (slug.StartsWith(\"DONE-\", ...) ||\n            slug.StartsWith(\"IGNORE-\", ...) ||\n            slug is \"completed\" or \"templates\")\n        {\n            return $\"Slug \\\"{slug}\\\" is reserved (DONE-/IGNORE- prefix, or the completed/templates folder name). Choose a different name.\";\n        }\n```\n\n### 2e. `src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs`\nLine 249 (in `OpenNewTaskDialog()` open branch, after `NewTaskError = null;`): insert `PrepareNewTaskTemplates();`.\n\n### 2f. `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml`\nIn the new-task grid (lines 173-205), change `RowDefinitions=\"Auto,*,Auto\"` → `RowDefinitions=\"Auto,Auto,*,Auto\"`, insert the template dropdown StackPanel before the title TextBox, shift existing rows: title→Row=\"1\", body TextBox→Row=\"2\", error TextBlock→Row=\"3\".\n\n### 2g. `README.md`\nInsert the \"# Task Templates\" section (per spec §9) between the \"# What Visual Relay Does\" section (ends line 65) and \"# Tests\" (line 67).\n\n## Stage 3: New test files\n\n### 3a. Create `tests/VisualRelay.Tests/TaskTemplatesTests.cs`\nSealed class, plain `[Fact]`s:\n- `Parse_Frontmatter_ExtractsNameTitleAndBody`\n- `Parse_NoFrontmatter_WholeContentIsBody_NameFallsBackToId`\n- `Parse_CrLf_NormalizedBeforeParsing`\n- `Parse_UnknownFrontmatterKeys_Ignored`\n- `Parse_UnclosedFrontmatter_TreatedAsBody`\n- `Load_BuiltIns_BlankFirstThenSpeedUp` (assert index [0] is blank, [1] is speed-up, pinned content checks)\n- `Load_UserOverridesBuiltIn_RepoOverridesUser`\n- `Load_NewIdsFromBothLayers_AppearSortedByName`\n- `Load_UnreadableEntrySkipped`\n- `SpeedUpTemplate_PinsLoadBearingContent` (Contains pinned lines, DoesNotContain \"dotnet\")\n\n### 3b. Create `tests/VisualRelay.Tests/NewTaskAuthoringTests.Templates.cs`\nNew partial of `public sealed partial class NewTaskAuthoringTests` (no `[Collection]` attribute — the existing partial `NewTaskAuthoringTests.cs` already has it). `[AvaloniaFact]`s:\n- `OpenNewTaskDialog_ListsBuiltInsWithBlankSelected`\n- `SelectingSpeedUpTemplate_PrefillsTitleAndBody`\n- `TemplateChange_NeverClobbersUserEditedField`\n- `RepoTemplate_OverridesBuiltInBlank`\n- `UserTemplate_AppearsInDropdown`\n\n### 3c. Create `tests/VisualRelay.Tests/RelayTaskRepositoryTemplatesDirTests.cs`\nStandalone sealed class — `RelayTaskRepositoryTests.cs` is at 300/300. One fact:\n- `ListPendingAsync_SkipsTemplatesDirectory` — writes task `alpha` and `templates/skeleton`, asserts only `alpha` returned.\n\n### 3d. Create `tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.TemplatesGrant.cs`\nNew partial of `SwivalSubagentRunnerSandboxTests`. One fact:\n- `BuildNonoPrefix_GrantsUserTemplatesDir` — call with `userTemplatesDirOverride: <tempdir>/templates`, assert `-a` and `<tempdir>/templates` present in args after `SandboxExtraAllowPaths` pairs, assert directory created.\n\n## Stage 4: Update existing test files (arg-assertion fallout)\n\n### 4a. `tests/VisualRelay.Tests/RelayTaskWriterTests.cs` (257→~270 lines)\nAdd `ValidateSlug_RejectsReservedFolderNames` — `ValidateSlug(\"templates\")` and `ValidateSlug(\"completed\")` return error with \"reserved\"; `ValidateSlug(\"template\")` passes.\n\n### 4b. `tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs` (270 lines)\nAdd `private static string TemplatesDir => TaskTemplates.ResolveUserTemplatesDir();`. Update:\n- Line 41: `Take(9)` → `Take(11)`, array includes `\"-a\", TemplatesDir` after `\"--allow-cwd\"`\n- Lines 124-126: exact array includes `\"-a\", TemplatesDir`\n- Lines 137-139: exact array includes `\"-a\", TemplatesDir`\n- Lines 159-162: swivalPrefix indices shift +2: `[4]→[6]`, `[5]→[7]`, `[6]→[8]`, `[7]→[9]`\n- Line 165: verifyPrefix `[4]→[6]`, line 166 `[5]→[7]`\n- Lines 186-189: `prefix[4],[5]` stay (extra -a), `prefix[6]→[8]`, `prefix[7]→[9]`\n- Lines 207-212: `prefix[4],[5]` stay, `prefix[6]→[8]`, `prefix[7]→[9]`, `prefix[8]→[10]`, `prefix[9]→[11]`\n- Lines 248-249: `Take(9)` → `Take(11)`, array includes templates pair\n\n### 4c. `tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.SkipDirs.cs` (88 lines)\nAdd `TemplatesDir` property. Update lines 51 and 64: exact arrays include `\"-a\", TemplatesDir`.\n\n### 4d. `tests/VisualRelay.Tests/SandboxExtraAllowPathsConfigTests.cs` (262 lines)\nAdd `TemplatesDir`. Update:\n- Lines 230-232: `prefix[4],[5]` unchanged (extra), `prefix[6]` becomes `\"-a\"` (templates), add `Assert.Equal(TemplatesDir, prefix[7])`, `prefix[6]` was `\"--rollback\"` → now `prefix[8]`\n- Lines 247-250: `prefix[4],[5]` unchanged, add templates assertions at indices [6],[7], `\"--silent\"` → `prefix[8]`, `\"--\"` → `prefix[9]`\n\n### 4e. `tests/VisualRelay.Tests/SandboxDiagnosticsToggleTests.cs` (131 lines)\nSurvives with zero changes: `Take(4)` at line 58 still matches (templates grant at index 4+); `Where(a => a != \"--silent\")` filter at line 64 survives (both prefixes get same templates pair).\n\n### 4f. `tests/VisualRelay.Tests/NonoLaunchDriftGuardTests.cs` (77 lines)\nSurvives with zero changes: `Take(4)` at line 26 survives; filter equality at lines 36-37 survives (both prefixes get identical templates grant).\n\n### 4g. `tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs` (268 lines)\nAdd `TemplatesDir`. Update:\n- Line 25: `Take(6)` → `Take(8)`, array includes `\"-a\", TemplatesDir`\n- Line 48: `Take(6)` → `Take(8)`\n- Line 64: exact array includes `\"-a\", TemplatesDir`\n- Line 73: exact array includes `\"-a\", TemplatesDir`\n- Lines 83-91: swi indices `[4]→[6]`, `[5]→[7]`, `[6]→[8]`, `[7]→[9]`; ver indices `[4]→[6]`, `[5]→[7]`\n- Lines 116-119: `prefix[4],[5]` stay; `prefix[6]→[8]`, `prefix[7]→[9]`\n\n## Verification\n1. `dotnet build` — clean\n2. Run targeted test filter per spec — all green\n3. Full suite green\n4. `./visual-relay check` passes — zero new InspectCode findings, file-size budgets within limits",
  "manifest": [
    "+packaging/task-templates/blank.md",
    "+packaging/task-templates/speed-up-automated-tests.md",
    "+src/VisualRelay.Core/Tasks/TaskTemplates.cs",
    "+src/VisualRelay.App/ViewModels/MainWindowViewModel.Templates.cs",
    "src/VisualRelay.Core/VisualRelay.Core.csproj",
    "src/VisualRelay.Core/Execution/ProcessRunners.cs",
    "src/VisualRelay.Core/Tasks/RelayTaskRepository.cs",
    "src/VisualRelay.Core/Tasks/RelayTaskWriter.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs",
    "src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml",
    "README.md",
    "+tests/VisualRelay.Tests/TaskTemplatesTests.cs",
    "+tests/VisualRelay.Tests/NewTaskAuthoringTests.Templates.cs",
    "+tests/VisualRelay.Tests/RelayTaskRepositoryTemplatesDirTests.cs",
    "+tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.TemplatesGrant.cs",
    "tests/VisualRelay.Tests/RelayTaskWriterTests.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.SkipDirs.cs",
    "tests/VisualRelay.Tests/SandboxExtraAllowPathsConfigTests.cs",
    "tests/VisualRelay.Tests/SandboxDiagnosticsToggleTests.cs",
    "tests/VisualRelay.Tests/NonoLaunchDriftGuardTests.cs",
    "tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 7, untracked deleted: 4.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/TaskTemplatesTests.cs",
    "tests/VisualRelay.Tests/NewTaskAuthoringTests.Templates.cs",
    "tests/VisualRelay.Tests/RelayTaskRepositoryTemplatesDirTests.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.TemplatesGrant.cs",
    "tests/VisualRelay.Tests/RelayTaskWriterTests.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.SkipDirs.cs",
    "tests/VisualRelay.Tests/SandboxExtraAllowPathsConfigTests.cs",
    "tests/VisualRelay.Tests/SandboxDiagnosticsToggleTests.cs",
    "tests/VisualRelay.Tests/NonoLaunchDriftGuardTests.cs",
    "tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs"
  ],
  "rationale": "All 125 targeted tests pass (1 skipped: Windows-only UTF-8 test). Implemented the three-layer template system: built-in templates embedded as resources in VisualRelay.Core, user templates in ~/.config/visual-relay/templates/, repo templates in llm-tasks/templates/. The Template dropdown pre-fills title/body without clobbering user edits. Updated all 6 existing arg-assertion test files to include the new `-a <templatesDir>` pair that BuildNonoPrefix now emits for the sandbox write grant. All file-size budgets respected (ProcessRunners.cs=194, RelayTaskWriter.cs=299, Authoring.cs=295, TaskDetailPanel.axaml=289, TaskTemplates.cs=160, Templates.cs=81). No changes to MainWindowViewModel.cs, CreateNewTaskAsync, CodeChangeGate.cs, WorktreeFilter.cs, WorktreeResetter.cs, GitCommitter.Untracked.cs, or vr-guard.json."
}

## Stage 6 - Implement

{ "summary": "All 3056 tests pass (113 environment-dependent skipped, 0 failed). File budgets all respected: MainWindowViewModel.cs=300/300, Authoring.cs=295/295, Templates.cs=81/300, TaskDetailPanel.axaml=289/292, RelayTaskWriter.cs=299/299, RelayTaskRepository.cs=291/291, ProcessRunners.cs=194/200, TaskTemplates.cs=136/300. Implemented the three-layer (built-in/user/repo) new-task template system with Template dropdown in the New-task dialog, sandbox write grant for user templates dir, reserved-name validation for 'completed'/'templates' slugs, and templates directory skip in task discovery. Updated all 6 existing arg-assertion test files for the new -a <templatesDir> pair in BuildNonoPrefix. Fixed Parse to only extract frontmatter key-values when the closing --- exists (unclosed frontmatter → whole content is body, Name falls back to id)." }

## Stage 8 - Visual-review

{
  "verdict": "pass",
  "issues": []
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "All 3056 tests pass (0 failed, 113 skipped). Every file is within its line budget: MainWindowViewModel.cs=300/300 (untouched), Authoring.cs=295/295, Templates.cs=81/300, TaskDetailPanel.axaml=289/292, RelayTaskWriter.cs=299/299, RelayTaskRepository.cs=291/291, ProcessRunners.cs=194/200, TaskTemplates.cs=136/300. The three-layer (built-in/user/repo) new-task template system is fully implemented: two built-in templates (Blank, Create Tasks to Speed Up Automated Tests) embedded as resources in VisualRelay.Core; user templates resolved via XdgConfig to ~/.config/visual-relay/templates/; repo templates at llm-tasks/templates/. TaskTemplates.Load resolves by id with repo>user>built-in precedence and frontmatter parsing. The Template dropdown in TaskDetailPanel prefills title/body without clobbering user edits via last-applied tracking. BuildNonoPrefix grants the user templates dir via -a flag with optional userTemplatesDirOverride for tests. Reserved slugs \"completed\" and \"templates\" are rejected in ValidateSlug. Task discovery skips the templates subdirectory. All 6 existing arg-assertion test files updated for the new -a pair. No changes to MainWindowViewModel.cs, CreateNewTaskAsync, CodeChangeGate.cs, WorktreeFilter.cs, WorktreeResetter.cs, GitCommitter.Untracked.cs, or vr-guard.json.",
  "commitMessages": [
    "feat: add three-layer new-task template system with built-in/user/repo support",
    "feat: implement TaskTemplates loader with frontmatter parsing and sandbox write grant",
    "feat: add Template dropdown to New-task dialog — prefills title/body, never clobbers edits",
    "feat: reserve 'completed' and 'templates' folder names, skip templates dir in task discovery",
    "feat: ship two built-in task templates (Blank, Speed Up Automated Tests) as embedded resources"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

